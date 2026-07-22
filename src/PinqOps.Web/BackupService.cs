using System.Text.Json;
using PinqOps.Backups;

namespace PinqOps.Web;

/// <summary>
/// Runs and restores backups of database containers and docker volumes.
/// Database dumps use the container's own tools and credentials (read from its
/// environment via <c>sh -c</c>, so no password is ever passed on the command
/// line); volumes are tarred through a throwaway alpine container. Snapshots
/// land under <c>/opt/pinqops/backups/&lt;target&gt;/&lt;timestamp&gt;.&lt;ext&gt;</c>.
/// </summary>
public sealed class BackupService
{
    public const string BackupRoot = "/opt/pinqops/backups";
    private const long MinFreeBytes = 500L * 1024 * 1024; // refuse to start a backup under 500 MB free

    private readonly DockerService _docker;
    private readonly SystemInfoService _system;
    private readonly ILogger<BackupService> _logger;
    private readonly string _lastRunPath = Path.Combine(BackupRoot, "lastRun.json");

    public BackupService(DockerService docker, SystemInfoService system, ILogger<BackupService> logger)
    {
        _docker = docker;
        _system = system;
        _logger = logger;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _running = new();

    public string TargetDirectory(string targetId) => Path.Combine(BackupRoot, targetId);

    public bool IsRunning(string targetId) => _running.ContainsKey(targetId);

    /// <summary>Runs a backup, refusing to overlap another run of the same target.</summary>
    public async Task<object> RunGuardedAsync(BackupTarget target)
    {
        if (!_running.TryAdd(target.Id, 0))
        {
            throw new InvalidOperationException("A backup for this target is already running.");
        }

        try
        {
            return await BackupAsync(target);
        }
        finally
        {
            _running.TryRemove(target.Id, out _);
        }
    }

    /// <summary>Runs one backup, prunes old snapshots, and records the run time.</summary>
    public async Task<object> BackupAsync(BackupTarget target)
    {
        if (!BackupNaming.IsValidId(target.Id))
        {
            throw new ArgumentException($"'{target.Id}' is not a valid backup id.");
        }

        if (_system.RootFreeBytes() is { } free && free < MinFreeBytes)
        {
            throw new InvalidOperationException(
                $"Only {free / 1024 / 1024} MB free on disk — free some space before backing up.");
        }

        var directory = TargetDirectory(target.Id);
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.UtcNow;
        var fileName = BackupNaming.FileName(target.Engine, timestamp);
        var hostPath = Path.Combine(directory, fileName);

        if (target.Kind == "volume" || target.Engine == "volume")
        {
            await _docker.BackupVolumeAsync(target.Name, directory, fileName);
        }
        else
        {
            await DumpDatabaseAsync(target, hostPath);
        }

        Prune(target);
        SetLastRun(target.Id, timestamp);
        var size = File.Exists(hostPath) ? new FileInfo(hostPath).Length : 0;
        _logger.LogWarning("Backup of {Target} → {File} ({Size} bytes)", target.Id, fileName, size);
        return new { ok = true, snapshot = fileName, sizeBytes = size };
    }

    private async Task DumpDatabaseAsync(BackupTarget target, string hostPath)
    {
        // Each dump writes to a temp file inside the container, then docker cp
        // brings it out — this never buffers a large dump in the dashboard.
        var (dump, containerFile) = DumpPlan(target.Engine);
        await _docker.ExecAsync(target.Name, dump);
        await _docker.CopyFromContainerAsync(target.Name, containerFile, hostPath);
        if (!containerFile.StartsWith("/data/", StringComparison.Ordinal)) // don't delete redis's live dump.rdb
        {
            try
            {
                await _docker.ExecAsync(target.Name, "rm", "-f", containerFile);
            }
            catch (InvalidOperationException)
            {
                // Best-effort cleanup of the in-container temp file.
            }
        }
    }

    /// <summary>The in-container dump command and the file it produces, per engine.</summary>
    public static (string[] Command, string ContainerFile) DumpPlan(string engine) => engine switch
    {
        "postgres" => (["pg_dumpall", "-U", "postgres", "-f", "/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mysql" => (["sh", "-c", "mysqldump -uroot -p\"$MYSQL_ROOT_PASSWORD\" --all-databases --result-file=/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mariadb" => (["sh", "-c", "mariadb-dump -uroot -p\"$MARIADB_ROOT_PASSWORD\" --all-databases --result-file=/tmp/pinqops-backup.sql"], "/tmp/pinqops-backup.sql"),
        "mongo" => (["mongodump", "--archive=/tmp/pinqops-backup.archive"], "/tmp/pinqops-backup.archive"),
        "redis" => (["redis-cli", "SAVE"], "/data/dump.rdb"),
        _ => throw new ArgumentException($"Backups are not supported for engine '{engine}'."),
    };

    /// <summary>The in-container restore command, per engine (reads /tmp/pinqops-restore.*).</summary>
    public static string[] RestorePlan(string engine) => engine switch
    {
        "postgres" => ["sh", "-c", "psql -U postgres -f /tmp/pinqops-restore.sql postgres"],
        "mysql" => ["sh", "-c", "mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" < /tmp/pinqops-restore.sql"],
        "mariadb" => ["sh", "-c", "mariadb -uroot -p\"$MARIADB_ROOT_PASSWORD\" < /tmp/pinqops-restore.sql"],
        "mongo" => ["mongorestore", "--archive=/tmp/pinqops-restore.archive", "--drop"],
        _ => throw new ArgumentException($"Restore is not supported for engine '{engine}'."),
    };

    public async Task RestoreAsync(BackupTarget target, string snapshot)
    {
        if (!BackupNaming.IsValidSnapshot(snapshot))
        {
            throw new ArgumentException("Invalid snapshot name.");
        }

        var hostPath = Path.Combine(TargetDirectory(target.Id), snapshot);
        if (!File.Exists(hostPath))
        {
            throw new ArgumentException("That snapshot no longer exists.");
        }

        if (target.Kind == "volume" || target.Engine == "volume")
        {
            await _docker.RestoreVolumeAsync(target.Name, TargetDirectory(target.Id), snapshot);
        }
        else if (target.Engine == "redis")
        {
            // Redis loads its RDB at startup: stop, replace the file, start.
            await _docker.ContainerActionAsync(target.Name, "stop");
            await _docker.CopyToContainerAsync(hostPath, target.Name, "/data/dump.rdb");
            await _docker.ContainerActionAsync(target.Name, "start");
        }
        else
        {
            var containerFile = $"/tmp/pinqops-restore.{BackupNaming.Extension(target.Engine)}";
            await _docker.CopyToContainerAsync(hostPath, target.Name, containerFile);
            await _docker.ExecAsync(target.Name, RestorePlan(target.Engine));
            try
            {
                await _docker.ExecAsync(target.Name, "rm", "-f", containerFile);
            }
            catch (InvalidOperationException)
            {
            }
        }

        _logger.LogWarning("Restored {Target} from {Snapshot}", target.Id, snapshot);
    }

    public IReadOnlyList<object> ListSnapshots(string targetId)
    {
        var directory = TargetDirectory(targetId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return new DirectoryInfo(directory).GetFiles()
            .Where(file => BackupNaming.IsValidSnapshot(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => (object)new { name = file.Name, sizeBytes = file.Length, at = file.CreationTimeUtc })
            .ToList();
    }

    public void DeleteSnapshot(string targetId, string snapshot)
    {
        if (!BackupNaming.IsValidId(targetId) || !BackupNaming.IsValidSnapshot(snapshot))
        {
            throw new ArgumentException("Invalid snapshot.");
        }

        var path = Path.Combine(TargetDirectory(targetId), snapshot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>A validated absolute path for downloading a snapshot, or null.</summary>
    public string? SnapshotPath(string targetId, string snapshot)
    {
        if (!BackupNaming.IsValidId(targetId) || !BackupNaming.IsValidSnapshot(snapshot))
        {
            return null;
        }

        var path = Path.Combine(TargetDirectory(targetId), snapshot);
        return File.Exists(path) ? path : null;
    }

    private void Prune(BackupTarget target)
    {
        if (target.RetentionCount <= 0 || !Directory.Exists(TargetDirectory(target.Id)))
        {
            return;
        }

        var stale = new DirectoryInfo(TargetDirectory(target.Id)).GetFiles()
            .Where(file => BackupNaming.IsValidSnapshot(file.Name))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(target.RetentionCount);
        foreach (var file in stale)
        {
            file.Delete();
        }
    }

    // ---- last-run state -----------------------------------------------------------

    public DateTimeOffset? LastRun(string targetId) => LoadLastRun().GetValueOrDefault(targetId);

    private void SetLastRun(string targetId, DateTimeOffset at)
    {
        var map = LoadLastRun();
        map[targetId] = at;
        Directory.CreateDirectory(BackupRoot);
        File.WriteAllText(_lastRunPath, JsonSerializer.Serialize(map));
    }

    private Dictionary<string, DateTimeOffset> LoadLastRun()
    {
        try
        {
            if (File.Exists(_lastRunPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_lastRunPath))
                    ?? [];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }
}
