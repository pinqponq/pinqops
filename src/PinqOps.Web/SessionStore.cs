using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>The signed-in identity a valid session token resolves to.</summary>
public sealed record SessionPrincipal(string Username, string Role);

/// <summary>In-memory bearer-token sessions for the dashboard (24h sliding).</summary>
public sealed class SessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxSessions = 256;

    private sealed record Session(DateTimeOffset Expiry, string Username, string Role);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>Opens a session for a signed-in user and returns its bearer token.</summary>
    public string Create(string username, string role)
    {
        PruneExpired();

        // Cap the table so leaked/abandoned logins cannot grow it without bound;
        // evicting the oldest session only forces that client to log in again.
        while (_sessions.Count >= MaxSessions)
        {
            var oldest = _sessions.MinBy(pair => pair.Value.Expiry);
            _sessions.TryRemove(oldest.Key, out _);
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new Session(DateTimeOffset.UtcNow + Lifetime, username, role);
        return token;
    }

    /// <summary>The session's identity if the token is valid (and slides its expiry), else null.</summary>
    public SessionPrincipal? Resolve(string token)
    {
        if (!_sessions.TryGetValue(token, out var session))
        {
            return null;
        }

        if (session.Expiry < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        _sessions[token] = session with { Expiry = DateTimeOffset.UtcNow + Lifetime };
        return new SessionPrincipal(session.Username, session.Role);
    }

    public bool Validate(string token) => Resolve(token) is not null;

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>Signs every session out — used when the password changes.</summary>
    public void RevokeAll() => _sessions.Clear();

    /// <summary>Signs a specific user out everywhere — used when their role changes or they are removed.</summary>
    public void RevokeUser(string username)
    {
        foreach (var (token, session) in _sessions)
        {
            if (string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, session) in _sessions)
        {
            if (session.Expiry < now)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }
}
