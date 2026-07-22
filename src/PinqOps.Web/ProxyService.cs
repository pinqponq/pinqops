using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// Manages the optional reverse proxy (a Caddy container) that gives deployed
/// apps real domains with automatic Let's Encrypt TLS. The proxy forwards to
/// each app by container name over the shared <c>pinqops-apps</c> network, so
/// domain access and the plain <c>host:port</c> publish coexist — an app with no
/// domain is unaffected. The dashboard owns the Caddyfile; the container only
/// reloads it.
/// </summary>
public sealed class ProxyService
{
    public const string ContainerName = "pinqops-proxy";
    public const string Image = "caddy:2-alpine";
    public const string Directory = ProxyPaths.DefaultDirectory;

    private readonly DockerService _docker;
    private readonly DomainConfigStore _store = new(Directory);
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static (DateTimeOffset At, string? Ip) _publicIpCache;

    public ProxyService(DockerService docker) => _docker = docker;

    public DomainConfigStore Store => _store;

    public async Task<object> StatusAsync()
    {
        var (exists, running) = await _docker.ContainerStateAsync(ContainerName);
        var config = _store.Load();
        // Once our proxy owns 80/443 the bind probe reports them busy — that is
        // expected, not a conflict, so only probe when the proxy is not running.
        var portsFree = running || (HostPort.IsAvailable(80) && HostPort.IsAvailable(443));
        return new
        {
            installed = exists,
            running,
            ports80443Free = portsFree,
            // A non-root dashboard cannot bind privileged ports, so a "busy"
            // result may be a permission artifact rather than a real conflict.
            probeUnreliable = !running && !portsFree && !IsRoot(),
            acmeEmail = config.AcmeEmail,
            staging = config.UseStagingCa,
            domainsCount = config.Domains.Count,
            caddyfilePath = ProxyPaths.CaddyfilePath(Directory),
        };
    }

    public async Task<object> InstallAsync(string? acmeEmail, bool staging, bool force)
    {
        var (exists, _) = await _docker.ContainerStateAsync(ContainerName);
        if (!exists && !force && !(HostPort.IsAvailable(80) && HostPort.IsAvailable(443)))
        {
            throw new InvalidOperationException(
                "Port 80 or 443 is already in use on this server — free it (is another web server running?) "
                + "before installing the proxy. If pinqops runs as a non-root user, privileged ports can look "
                + "busy even when free; retry to install anyway.");
        }

        System.IO.Directory.CreateDirectory(Directory);
        _store.Save(WithAcme(_store.Load(), acmeEmail, staging));
        await WriteCaddyfileAsync();

        if (exists)
        {
            // Reinstall = pick up the new global settings without a fresh container.
            return await ApplyAsync();
        }

        var output = await _docker.InstallProxyAsync(ContainerName, Image, ProxyPaths.CaddyfilePath(Directory));
        return new { ok = true, output };
    }

    /// <summary>
    /// Regenerates the Caddyfile and hot-reloads it (no downtime) when the proxy
    /// is running. If it is not installed yet the file is still written, so the
    /// routes are already correct the moment it is installed.
    /// </summary>
    public async Task<object> ApplyAsync()
    {
        await WriteCaddyfileAsync();
        var (exists, _) = await _docker.ContainerStateAsync(ContainerName);
        if (!exists)
        {
            return new { ok = true, reloaded = false };
        }

        var output = await _docker.ExecAsync(ContainerName, "caddy", "reload", "--config", "/etc/caddy/Caddyfile");
        return new { ok = true, reloaded = true, output };
    }

    /// <summary>
    /// Advisory DNS preflight: does the domain resolve to one of this server's
    /// addresses? A mismatch is a warning (the cert will fail until DNS points
    /// here), never a hard block — a domain could sit behind a CDN.
    /// </summary>
    public async Task<DnsCheckResult> CheckDnsAsync(string domain)
    {
        var normalized = DomainName.Normalize(domain);
        string[] resolved;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(normalized);
            resolved = addresses.Select(a => a.ToString()).ToArray();
        }
        catch (SocketException)
        {
            resolved = [];
        }

        var serverIps = LocalAddresses();
        var publicIp = await PublicIpAsync();
        if (publicIp is not null && !serverIps.Contains(publicIp))
        {
            serverIps = [.. serverIps, publicIp];
        }

        var matches = resolved.Any(r => serverIps.Contains(r));
        return new DnsCheckResult(normalized, resolved, serverIps, publicIp, matches);
    }

    private async Task WriteCaddyfileAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);
        await File.WriteAllTextAsync(ProxyPaths.CaddyfilePath(Directory), CaddyfileGenerator.Generate(_store.Load()));
    }

    private static DomainConfig WithAcme(DomainConfig config, string? acmeEmail, bool staging)
    {
        if (acmeEmail is not null)
        {
            config.AcmeEmail = acmeEmail.Trim();
        }

        config.UseStagingCa = staging;
        return config;
    }

    private static string[] LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => !IPAddress.IsLoopback(address)
                && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
                && !address.IsIPv6LinkLocal)
            .Select(address => address.ToString())
            .Distinct()
            .ToArray();

    private static async Task<string?> PublicIpAsync()
    {
        if (_publicIpCache.Ip is not null && DateTimeOffset.UtcNow - _publicIpCache.At < TimeSpan.FromMinutes(10))
        {
            return _publicIpCache.Ip;
        }

        try
        {
            var ip = (await PublicIpClient.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(ip, out _))
            {
                _publicIpCache = (DateTimeOffset.UtcNow, ip);
                return ip;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // NAT'd server with no reachable metadata service — the NIC addresses
            // are the best we have.
        }

        return _publicIpCache.Ip;
    }

    // A good-enough heuristic: only root can bind privileged ports on Linux.
    private static bool IsRoot() => Environment.UserName == "root";
}

/// <summary>Advisory DNS preflight result for a domain.</summary>
public sealed record DnsCheckResult(
    string Domain, string[] ResolvedIps, string[] ServerIps, string? PublicIp, bool Matches);
