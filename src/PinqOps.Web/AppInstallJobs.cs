using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// In-memory registry of app-install jobs. Installs run in the background
/// (docker pull can take minutes) and the UI polls a job until it's done, so
/// progress shows without a page refresh. Jobs are pruned after a retention
/// window; at most one job runs per app at a time.
/// </summary>
public sealed class AppInstallJobs
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public sealed class Job
    {
        public required string Id { get; init; }
        public required string AppId { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

        // "pulling" → "starting" → "done" | "error"
        public volatile string Phase = "pulling";
        public volatile string? Output;
        public volatile string? Error;

        public bool Done => Phase is "done" or "error";
    }

    /// <summary>
    /// Starts tracking a new job for <paramref name="appId"/>, or returns null
    /// when an install for that app is already running.
    /// </summary>
    public Job? TryStart(string appId)
    {
        Prune();
        if (_jobs.Values.Any(job => !job.Done && string.Equals(job.AppId, appId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var job = new Job
        {
            Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
            AppId = appId,
        };
        _jobs[job.Id] = job;
        return job;
    }

    public Job? Find(string jobId)
    {
        Prune();
        return _jobs.GetValueOrDefault(jobId);
    }

    /// <summary>App ids with an install currently in flight (for list badges).</summary>
    public IReadOnlyList<string> ActiveAppIds()
    {
        Prune();
        return _jobs.Values.Where(job => !job.Done).Select(job => job.AppId).ToList();
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.Done && job.StartedAt < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }
}
