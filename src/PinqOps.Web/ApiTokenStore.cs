using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>One API token's stored metadata (never its plaintext).</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>read | deploy | admin.</summary>
    public string Scope { get; set; } = "read";

    /// <summary>Hex SHA-256 of the full token — the lookup key.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Last 4 chars of the token, for display only.</summary>
    public string Last4 { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Personal API tokens for the REST API — usable by any HTTP client or AI agent
/// (an OpenAI function-calling tool, a Claude MCP server, curl, CI). A token is
/// high-entropy, so it is stored as a fast SHA-256 hash (not PBKDF2) keyed for
/// O(1) lookup; the plaintext <c>pot_…</c> is shown once at creation.
/// </summary>
public sealed class ApiTokenStore
{
    public const string Prefix = "pot_";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public ApiTokenStore(string path) => _path = path;

    public static bool LooksLikeToken(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Mints a token, stores its hash, and returns the plaintext (once).</summary>
    public (ApiToken Token, string Plaintext) Create(string name, string scope, DateTimeOffset now)
    {
        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(6)),
            Name = string.IsNullOrWhiteSpace(name) ? "token" : name.Trim(),
            Scope = scope is "read" or "deploy" or "admin" ? scope : "read",
            Sha256 = Hash(secret),
            Last4 = secret[^4..],
            CreatedAt = now,
        };

        lock (_gate)
        {
            var all = LoadAll();
            all.Add(token);
            Save(all);
        }

        return (token, secret);
    }

    /// <summary>The token's scope if it is valid, else null. Touches LastUsedAt (throttled).</summary>
    public string? Validate(string presented, DateTimeOffset now)
    {
        var hash = Hash(presented);
        lock (_gate)
        {
            var all = LoadAll();
            var match = all.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(t.Sha256), Encoding.ASCII.GetBytes(hash)));
            if (match is null)
            {
                return null;
            }

            // Persist "last used" at most once a minute to avoid a write per call.
            if (match.LastUsedAt is null || now - match.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                match.LastUsedAt = now;
                Save(all);
            }

            return match.Scope;
        }
    }

    public IReadOnlyList<ApiToken> List()
    {
        lock (_gate)
        {
            return LoadAll();
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(t => t.Id == id) > 0;
            if (removed)
            {
                Save(all);
            }

            return removed;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private List<ApiToken> LoadAll()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<ApiToken>>(File.ReadAllText(_path), SerializerOptions) ?? [];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private void Save(List<ApiToken> tokens)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var isNew = !File.Exists(_path);
        File.WriteAllText(_path, JsonSerializer.Serialize(tokens, SerializerOptions));
        if (isNew && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Maps a request to the API scope it requires and compares scopes.</summary>
public static class ApiScopes
{
    // Writes that a "deploy" token may perform; everything else that writes is admin-only.
    private static readonly string[] DeployWritePrefixes =
    [
        "/api/deploy/rollback", "/api/setup/", "/api/compose/apply",
        "/api/apps/", "/api/backups/run", "/api/backups/restore",
    ];

    public static string RequiredFor(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            return "read";
        }

        foreach (var prefix in DeployWritePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return "deploy";
            }
        }

        return "admin";
    }

    public static bool Satisfies(string have, string need) => Rank(have) >= Rank(need);

    private static int Rank(string scope) => scope switch { "admin" => 3, "deploy" => 2, _ => 1 };
}
