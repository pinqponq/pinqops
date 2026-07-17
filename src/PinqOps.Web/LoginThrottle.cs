using System.Collections.Concurrent;

namespace PinqOps.Web;

/// <summary>
/// Per-client brute-force protection for password endpoints: after
/// <see cref="MaxFailures"/> failed attempts within the window, the client is
/// locked out for <see cref="Lockout"/>.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed record Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Time the client must still wait, or null if attempts are allowed.</summary>
    public TimeSpan? RetryAfter(string clientKey)
    {
        if (!_entries.TryGetValue(clientKey, out var entry))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (entry.LockedUntil is { } lockedUntil)
        {
            if (lockedUntil > now)
            {
                return lockedUntil - now;
            }

            _entries.TryRemove(clientKey, out _);
            return null;
        }

        if (now - entry.FirstFailureAt > Window)
        {
            _entries.TryRemove(clientKey, out _);
        }

        return null;
    }

    public void RecordFailure(string clientKey)
    {
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            clientKey,
            _ => new Entry(1, now, null),
            (_, entry) =>
            {
                if (now - entry.FirstFailureAt > Window)
                {
                    return new Entry(1, now, null);
                }

                var failures = entry.Failures + 1;
                return failures >= MaxFailures
                    ? new Entry(failures, entry.FirstFailureAt, now + Lockout)
                    : new Entry(failures, entry.FirstFailureAt, null);
            });

        // Opportunistic cleanup so the table cannot grow without bound.
        if (_entries.Count > 10_000)
        {
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.FirstFailureAt > Window && (entry.LockedUntil is null || entry.LockedUntil < now))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    public void RecordSuccess(string clientKey) => _entries.TryRemove(clientKey, out _);
}
