using System.Text.Json;

namespace PinqOps.Proxy;

/// <summary>
/// The managed reverse proxy's configuration: which domains route to which
/// containers, and the ACME account settings. Server-global (one proxy serves
/// every app and catalog service), stored as <c>/opt/pinqops/proxy/domains.json</c>
/// so both the dashboard and — later — the runner CLI can read and write it.
/// </summary>
public sealed class DomainConfig
{
    /// <summary>Contact e-mail for Let's Encrypt (recommended, not required).</summary>
    public string AcmeEmail { get; set; } = string.Empty;

    /// <summary>Use the LE staging CA (untrusted certs, no rate limit) for testing.</summary>
    public bool UseStagingCa { get; set; }

    public List<DomainEntry> Domains { get; set; } = [];
}

/// <summary>One domain routed to one container.</summary>
public sealed class DomainEntry
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>What this domain points at: an app id (slug), or <c>catalog:&lt;id&gt;</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The container name the proxy forwards to (resolved when added).</summary>
    public string TargetContainer { get; set; } = string.Empty;

    public int TargetPort { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Filesystem locations of the proxy's generated files.</summary>
public static class ProxyPaths
{
    public const string DefaultDirectory = "/opt/pinqops/proxy";

    public static string DomainsFile(string proxyDirectory) => Path.Combine(proxyDirectory, "domains.json");

    public static string CaddyfilePath(string proxyDirectory) => Path.Combine(proxyDirectory, "Caddyfile");
}

/// <summary>
/// Loads and saves <see cref="DomainConfig"/> (camelCase JSON, 0600). Writes are
/// atomic (temp + rename) and serialized with a short retry, because the
/// dashboard and the runner CLI may both write during a preview deploy.
/// </summary>
public sealed class DomainConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _path;

    public DomainConfigStore(string? proxyDirectory = null)
    {
        _directory = proxyDirectory ?? ProxyPaths.DefaultDirectory;
        _path = ProxyPaths.DomainsFile(_directory);
    }

    public string Path_ => _path;

    public DomainConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<DomainConfig>(File.ReadAllText(_path), SerializerOptions)
                    ?? new DomainConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt file means "no routes", never a crash.
        }

        return new DomainConfig();
    }

    public void Save(DomainConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(_directory);

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        // Per-process temp name so a concurrent dashboard/CLI writer can't clobber
        // this one's half-written temp; the rename is the atomic commit point.
        var temp = $"{_path}.{Environment.ProcessId}.tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temp, _path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Validation for a routable domain name.</summary>
public static class DomainName
{
    public static string Normalize(string? domain)
    {
        var value = (domain ?? string.Empty).Trim().ToLowerInvariant().TrimEnd('.');
        if (value.Length is 0 or > 253)
        {
            throw new ArgumentException("Enter a domain name (up to 253 characters).");
        }

        if (value.Contains('*'))
        {
            throw new ArgumentException("Wildcard domains are not supported — an HTTP-01 certificate cannot cover them.");
        }

        if (Uri.CheckHostName(value) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"'{domain}' is not a valid domain name.");
        }

        return value;
    }
}
