using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Dashboard-side deploy state and rollback jobs. Reads the same
/// <c>.pinqops</c> state the CLI writes (history, pinned tag) and runs
/// rollbacks as background jobs — one at a time, mirroring the app-install job
/// pattern so the UI can poll for progress.
/// </summary>
public sealed class DeployService
{
    private readonly IProcessRunner _processRunner;

    // Only one deploy/rollback may touch the compose project at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _jobLock = new();
    private Job? _currentJob;

    public DeployService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public sealed class Job
    {
        private readonly StringBuilder _log = new();
        private readonly object _logLock = new();

        public required string Id { get; init; }
        public required string Tag { get; init; }

        // "running" → "done" | "error"
        public volatile string Phase = "running";
        public volatile string? Error;

        public bool Done => Phase is "done" or "error";

        public void Add(string line)
        {
            lock (_logLock)
            {
                _log.AppendLine(line);
            }
        }

        public string Log()
        {
            lock (_logLock)
            {
                return _log.ToString();
            }
        }
    }

    public object GetState(string composeFilePath)
    {
        var currentTag = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
        var lastSuccessful = new DeployHistoryStore(composeFilePath).LastSuccessful();
        return new
        {
            currentTag,
            currentDeployedAt = lastSuccessful?.StartedAt,
            composeUsesTagVariable = ComposeUsesTagVariable(composeFilePath),
            rollbackInProgress = CurrentJob() is { Done: false },
        };
    }

    public IReadOnlyList<DeployRecord> History(string composeFilePath) =>
        new DeployHistoryStore(composeFilePath).Load();

    public Job? Find(string jobId) =>
        CurrentJob() is { } job && job.Id == jobId ? job : null;

    /// <summary>
    /// Starts a rollback to <paramref name="tag"/> in the background. The tag
    /// must appear in deploy history (any past deploy attempt of it counts), so
    /// arbitrary strings can never reach docker. Returns null when a rollback
    /// is already running.
    /// </summary>
    public Job? TryStartRollback(string composeFilePath, string tag)
    {
        if (!ComposeUsesTagVariable(composeFilePath))
        {
            throw new InvalidOperationException(
                $"{composeFilePath} does not reference ${{{Deployer.TagVariable}}}. "
                + $"Change the image line to e.g. 'image: ghcr.io/<owner>/<repo>:${{{Deployer.TagVariable}:-latest}}' first.");
        }

        var history = new DeployHistoryStore(composeFilePath);
        if (!history.Load().Any(record => record.Tag == tag))
        {
            throw new ArgumentException($"Tag '{tag}' is not in the deploy history.");
        }

        lock (_jobLock)
        {
            if (_currentJob is { Done: false })
            {
                return null;
            }

            var job = new Job
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                Tag = tag,
            };
            _currentJob = job;

            _ = Task.Run(() => RunRollbackAsync(job, composeFilePath, tag));
            return job;
        }
    }

    private async Task RunRollbackAsync(Job job, string composeFilePath, string tag)
    {
        await _gate.WaitAsync();
        try
        {
            var deployer = new Deployer(
                _processRunner,
                job.Add,
                history: new DeployHistoryStore(composeFilePath));
            var options = DeployOptions.Create(
                composeFilePath,
                tag: tag,
                trigger: DeployRecordValues.TriggerRollback);

            var succeeded = await deployer.DeployAsync(options);
            if (succeeded)
            {
                job.Phase = "done";
            }
            else
            {
                job.Error = "Rollback failed — see the log.";
                job.Phase = "error";
            }
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Phase = "error";
        }
        finally
        {
            _gate.Release();
        }
    }

    private Job? CurrentJob()
    {
        lock (_jobLock)
        {
            return _currentJob;
        }
    }

    internal static bool ComposeUsesTagVariable(string composeFilePath) =>
        File.Exists(composeFilePath)
        && File.ReadAllText(composeFilePath).Contains($"${{{Deployer.TagVariable}", StringComparison.Ordinal);
}
