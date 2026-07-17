using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>In-memory bearer-token sessions for the dashboard (24h sliding).</summary>
public sealed class SessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxSessions = 256;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public string Create()
    {
        PruneExpired();

        // Cap the table so leaked/abandoned logins cannot grow it without bound;
        // evicting the oldest session only forces that client to log in again.
        while (_sessions.Count >= MaxSessions)
        {
            var oldest = _sessions.MinBy(pair => pair.Value);
            _sessions.TryRemove(oldest.Key, out _);
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTimeOffset.UtcNow + Lifetime;
        return token;
    }

    public bool Validate(string token)
    {
        if (!_sessions.TryGetValue(token, out var expiry))
        {
            return false;
        }

        if (expiry < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = DateTimeOffset.UtcNow + Lifetime;
        return true;
    }

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>Signs every session out — used when the password changes.</summary>
    public void RevokeAll() => _sessions.Clear();

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, expiry) in _sessions)
        {
            if (expiry < now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }
}
