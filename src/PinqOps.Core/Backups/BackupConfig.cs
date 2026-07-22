using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinqOps.Backups;

/// <summary>
/// Scheduled backup targets. Server-global (one schedule per server), stored
/// next to <c>ui.json</c>, 0600.
/// </summary>
public sealed class BackupConfig
{
    public List<BackupTarget> Targets { get; set; } = [];
}

/// <summary>One thing to back up on a schedule: a database container or a volume.</summary>
public sealed class BackupTarget
{
    public string Id { get; set; } = string.Empty;

    /// <summary><c>db</c> or <c>volume</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The container name (db) or the docker volume name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>postgres | mysql | mariadb | mongo | redis | volume.</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>hourly | daily | weekly.</summary>
    public string Schedule { get; set; } = "daily";

    /// <summary>UTC hour (0-23) a daily/weekly backup runs at.</summary>
    public int AtHour { get; set; } = 3;

    /// <summary>How many snapshots to keep; older ones are pruned.</summary>
    public int RetentionCount { get; set; } = 7;

    public bool Enabled { get; set; } = true;
}

/// <summary>Whether a target is due to run now (pure, tick-driven).</summary>
public static class BackupSchedule
{
    public static bool IsDue(BackupTarget target, DateTimeOffset now, DateTimeOffset? lastRun) => target.Schedule switch
    {
        // Just under an hour so a per-minute tick fires once per hour without drift.
        "hourly" => lastRun is null || now - lastRun >= TimeSpan.FromMinutes(59),
        "weekly" => now.DayOfWeek == DayOfWeek.Monday && now.Hour == target.AtHour
            && (lastRun is null || now - lastRun >= TimeSpan.FromDays(6)),
        // daily (default): the >=23h guard keeps the AtHour window from double-firing.
        _ => now.Hour == target.AtHour && (lastRun is null || now - lastRun >= TimeSpan.FromHours(23)),
    };
}

/// <summary>Snapshot file naming and validation (path-traversal safe).</summary>
public static class BackupNaming
{
    private static readonly Regex SnapshotPattern = new(@"^\d{8}-\d{6}\.(sql|archive|rdb|tgz)$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static string Extension(string engine) => engine switch
    {
        "postgres" or "mysql" or "mariadb" => "sql",
        "mongo" => "archive",
        "redis" => "rdb",
        _ => "tgz",
    };

    public static string FileName(string engine, DateTimeOffset timestamp) =>
        $"{timestamp.UtcDateTime:yyyyMMdd-HHmmss}.{Extension(engine)}";

    /// <summary>A snapshot filename that is safe to join to a path.</summary>
    public static bool IsValidSnapshot(string name) => SnapshotPattern.IsMatch(name);

    /// <summary>A target id that is safe to use as a directory name.</summary>
    public static bool IsValidId(string id) => IdPattern.IsMatch(id);
}

/// <summary>Loads and saves <see cref="BackupConfig"/> (camelCase JSON, 0600).</summary>
public sealed class BackupConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public BackupConfigStore(string path) => _path = path;

    public string Path_ => _path;

    public BackupConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(_path), SerializerOptions)
                    ?? new BackupConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt config means "no scheduled backups", never a crash.
        }

        return new BackupConfig();
    }

    public void Save(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Atomic + owner-only (0600 from the first byte): the previous
        // File.WriteAllText left the mode unfixed on every save after the first,
        // and a torn write would be read back as corrupt and silently reset the
        // schedule.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
