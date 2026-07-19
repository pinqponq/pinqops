using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Manages the pinqops-managed Caddy reverse proxy: a single
/// <c>pinqops-caddy</c> container on the shared apps network publishing
/// 80/443, with its Caddyfile generated from the routes state and TLS
/// certificates persisted in named volumes across recreates.
/// </summary>
public sealed class CaddyService
{
    public const string ContainerName = "pinqops-caddy";
    public const string ManagedLabel = "pinqops.managed=caddy";
    public const string Image = "caddy:2-alpine";
    private const string CaddyfileMountPath = "/etc/caddy/Caddyfile";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _processRunner;
    private readonly DockerService _docker;
    private readonly CaddyRoutesStore _store;

    public CaddyService(IProcessRunner processRunner, DockerService docker, CaddyRoutesStore? store = null)
    {
        _processRunner = processRunner;
        _docker = docker;
        _store = store ?? new CaddyRoutesStore();
    }

    public CaddyRoutesStore Store => _store;

    public async Task<object> StatusAsync()
    {
        var routes = _store.Load();
        var inspect = await RunAsync(CommandTimeout, "inspect", "--format", "{{.State.Status}}", ContainerName)
            .ConfigureAwait(false);
        return new
        {
            installed = inspect.Succeeded,
            state = inspect.Succeeded ? inspect.StandardOutput.Trim() : null,
            email = routes.Email,
            routes = routes.Routes,
        };
    }

    /// <summary>
    /// Installs (or re-creates) the proxy container. Port conflicts on 80/443
    /// surface verbatim so the user sees which process holds them.
    /// </summary>
    public async Task<string> InstallAsync()
    {
        await _docker.EnsureSharedNetworkAsync().ConfigureAwait(false);
        WriteCaddyfile(_store.Load());

        // Idempotent: a leftover container (stopped or misconfigured) is
        // replaced; the cert volumes survive.
        await RunAsync(CommandTimeout, "rm", "-f", ContainerName).ConfigureAwait(false);

        var result = await RunAsync(
            InstallTimeout,
            "run", "-d",
            "--name", ContainerName,
            "--label", ManagedLabel,
            "--network", AppCatalog.SharedNetwork,
            "--restart", "unless-stopped",
            "-p", "80:80",
            "-p", "443:443",
            "-p", "443:443/udp",
            "-v", "pinqops-caddy-data:/data",
            "-v", "pinqops-caddy-config:/config",
            "-v", $"{_store.CaddyfilePath}:{CaddyfileMountPath}:ro",
            Image).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Regenerates the Caddyfile from state and hot-reloads the proxy; falls
    /// back to a container restart when <c>caddy reload</c> fails.
    /// </summary>
    public async Task ApplyAsync()
    {
        WriteCaddyfile(_store.Load());

        var reload = await RunAsync(CommandTimeout, "exec", ContainerName, "caddy", "reload", "--config", CaddyfileMountPath)
            .ConfigureAwait(false);
        if (reload.Succeeded)
        {
            return;
        }

        var restart = await RunAsync(CommandTimeout, "restart", ContainerName).ConfigureAwait(false);
        if (!restart.Succeeded)
        {
            throw Failed(reload);
        }
    }

    /// <summary>Removes the proxy container; certificate volumes are kept.</summary>
    public async Task<string> UninstallAsync()
    {
        var result = await RunAsync(CommandTimeout, "rm", "-f", ContainerName).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>True when the target container is attached to the shared apps network.</summary>
    public async Task<bool> IsOnSharedNetworkAsync(string containerName)
    {
        var inspect = await RunAsync(CommandTimeout, "inspect", "--format", "{{json .NetworkSettings.Networks}}", containerName)
            .ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(inspect.StandardOutput.Trim());
            return document.RootElement.TryGetProperty(AppCatalog.SharedNetwork, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteCaddyfile(CaddyRoutes routes)
    {
        Directory.CreateDirectory(_store.Directory_);
        File.WriteAllText(_store.CaddyfilePath, CaddyfileGenerator.Generate(routes));
    }

    private async Task<ProcessResult> RunAsync(TimeSpan timeout, params string[] arguments)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _processRunner.RunAsync("docker", arguments, workingDirectory: null, cts.Token).ConfigureAwait(false);
    }

    private static InvalidOperationException Failed(ProcessResult result) =>
        new(result.StandardError.Trim().Length > 0 ? result.StandardError.Trim() : $"docker exited with {result.ExitCode}");
}
