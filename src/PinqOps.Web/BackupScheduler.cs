using PinqOps.Backups;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's one background worker: every minute it runs any scheduled
/// backup that is due. Each run is fire-and-forget so a long dump neither blocks
/// the tick nor other targets, and <see cref="BackupService"/> refuses to
/// overlap two runs of the same target.
/// </summary>
public sealed class BackupScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly BackupService _backups;
    private readonly BackupConfigStore _store;
    private readonly ILogger<BackupScheduler> _logger;

    public BackupScheduler(BackupService backups, BackupConfigStore store, ILogger<BackupScheduler> logger)
    {
        _backups = backups;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Backup scheduler tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var target in _store.Load().Targets)
        {
            if (!target.Enabled
                || _backups.IsRunning(target.Id)
                || !BackupSchedule.IsDue(target, now, _backups.LastRun(target.Id)))
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _backups.RunGuardedAsync(target);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Scheduled backup of {Target} failed", target.Id);
                }
            });
        }
    }
}
