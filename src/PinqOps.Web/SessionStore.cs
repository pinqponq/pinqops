using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>In-memory bearer-token sessions for the dashboard (24h sliding).</summary>
public sealed class SessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public string Create()
    {
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
}
