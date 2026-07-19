using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinqOps.Web;

/// <summary>One reverse-proxy route: a public domain mapped to a container port.</summary>
public sealed record CaddyRoute(string Domain, string Target, int Port);

/// <summary>The proxy's persisted state (<c>~/.config/pinqops/caddy/routes.json</c>).</summary>
public sealed class CaddyRoutes
{
    /// <summary>ACME account email for Let's Encrypt.</summary>
    public string Email { get; set; } = string.Empty;

    public List<CaddyRoute> Routes { get; set; } = new();
}

/// <summary>Loads and saves <see cref="CaddyRoutes"/>, validating every field.</summary>
public sealed partial class CaddyRoutesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly Lock _gate = new();

    public CaddyRoutesStore(string? directory = null)
    {
        _directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "pinqops", "caddy");
    }

    public string Directory_ => _directory;
    public string RoutesFile => System.IO.Path.Combine(_directory, "routes.json");
    public string CaddyfilePath => System.IO.Path.Combine(_directory, "Caddyfile");

    public CaddyRoutes Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(RoutesFile))
                {
                    return JsonSerializer.Deserialize<CaddyRoutes>(File.ReadAllText(RoutesFile), SerializerOptions)
                        ?? new CaddyRoutes();
                }
            }
            catch (JsonException)
            {
                // Corrupt state must not brick the dashboard; start empty.
            }

            return new CaddyRoutes();
        }
    }

    public CaddyRoutes Update(Action<CaddyRoutes> mutate)
    {
        lock (_gate)
        {
            var routes = Load();
            mutate(routes);
            Directory.CreateDirectory(_directory);
            File.WriteAllText(RoutesFile, JsonSerializer.Serialize(routes, SerializerOptions));
            return routes;
        }
    }

    /// <summary>
    /// These values end up inside the Caddyfile; reject anything outside the
    /// strict grammar so no input can smuggle config directives.
    /// </summary>
    public static void Validate(CaddyRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ValidateDomain(route.Domain);

        if (string.IsNullOrWhiteSpace(route.Target)
            || !route.Target.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            throw new ArgumentException($"'{route.Target}' is not a valid container name.");
        }

        if (route.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be between 1 and 65535.");
        }
    }

    public static void ValidateDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || !DomainPattern().IsMatch(domain))
        {
            throw new ArgumentException($"'{domain}' is not a valid domain name.");
        }
    }

    public static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !EmailPattern().IsMatch(email))
        {
            throw new ArgumentException($"'{email}' is not a valid email address.");
        }
    }

    [GeneratedRegex(@"^(?=.{4,253}$)([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^[^@\s{}""]+@[^@\s{}""]+\.[^@\s{}""]+$")]
    private static partial Regex EmailPattern();
}
