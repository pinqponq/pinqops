using System.Diagnostics;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Read-mostly Docker access for the dashboard. Everything shells out to the
/// local <c>docker</c> CLI with fixed argument lists (no shell interpretation),
/// mirroring how the pinqops CLI drives Docker.
/// </summary>
public sealed class DockerService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] AllowedContainerActions = ["start", "stop", "restart", "pause", "unpause"];

    private readonly IProcessRunner _processRunner;

    public DockerService(IProcessRunner processRunner) => _processRunner = processRunner;

    public Task<List<JsonElement>> ListContainersAsync() =>
        JsonLinesAsync("ps", "-a", "--no-trunc", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListImagesAsync() =>
        JsonLinesAsync("images", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListVolumesAsync() =>
        JsonLinesAsync("volume", "ls", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListNetworksAsync() =>
        JsonLinesAsync("network", "ls", "--format", "{{json .}}");

    public async Task<JsonElement> InspectNetworkAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("network", "inspect", "--", name).ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : throw Failed(result);
    }

    private static readonly string[] AllowedNetworkDrivers = ["bridge", "overlay", "macvlan", "ipvlan"];

    public async Task<string> CreateNetworkAsync(string name, string? driver, bool isInternal)
    {
        ValidateResourceName(name);
        var arguments = new List<string> { "network", "create" };
        if (!string.IsNullOrWhiteSpace(driver))
        {
            if (!AllowedNetworkDrivers.Contains(driver))
            {
                throw new ArgumentException($"Unsupported network driver '{driver}'.");
            }

            arguments.AddRange(["--driver", driver]);
        }

        if (isInternal)
        {
            arguments.Add("--internal");
        }

        arguments.Add("--");
        arguments.Add(name);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> RemoveNetworkAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("network", "rm", "--", name).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> ConnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "connect", "--", network, container).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> DisconnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "disconnect", "--", network, container).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public Task<List<JsonElement>> StatsAsync() =>
        JsonLinesAsync("stats", "--no-stream", "--format", "{{json .}}");

    public Task<List<JsonElement>> SystemDiskUsageAsync() =>
        JsonLinesAsync("system", "df", "--format", "{{json .}}");

    public async Task<JsonElement?> VersionAsync()
    {
        var result = await RunAsync("version", "--format", "{{json .}}").ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : null;
    }

    public Task<List<JsonElement>> ComposeServicesAsync(string composeFile) =>
        JsonLinesAsync("compose", "-f", composeFile, "ps", "-a", "--format", "json");

    public async Task<string> ContainerLogsAsync(string containerId, int tail)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("logs", "--tail", tail.ToString(), "--timestamps", "--", containerId).ConfigureAwait(false);
        // Docker writes app output to both streams; show them together like the terminal does.
        return result.Succeeded || result.StandardError.Length > 0 || result.StandardOutput.Length > 0
            ? result.StandardOutput + result.StandardError
            : throw Failed(result);
    }

    public async Task<JsonElement> InspectContainerAsync(string containerId)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("inspect", "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : throw Failed(result);
    }

    public async Task<string> ContainerActionAsync(string containerId, string action)
    {
        ValidateResourceName(containerId);
        if (!AllowedContainerActions.Contains(action))
        {
            throw new ArgumentException($"Unsupported container action '{action}'.");
        }

        var result = await RunAsync(action, "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> PruneImagesAsync()
    {
        var result = await RunAsync("image", "prune", "-f").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Pulls an app image up front, so install progress can report the slow
    /// pull phase separately from the (fast) container start. Installs run as
    /// background jobs, so the leash only guards against a truly hung pull —
    /// large images on slow uplinks legitimately take tens of minutes.
    /// </summary>
    public async Task<string> PullImageAsync(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        var result = await RunAsync(TimeSpan.FromMinutes(30), "pull", image).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Runs a catalog app as a labeled, named container on the shared
    /// pinqops-apps network. Each entry in <paramref name="hostPorts"/>
    /// overrides the corresponding catalog port (0/absent keeps the default).
    /// </summary>
    public async Task<string> InstallAppAsync(AppSpec app, IReadOnlyList<int>? hostPorts, IReadOnlyList<string>? envOverride = null)
    {
        if (hostPorts is not null && hostPorts.Any(port => port is not 0 and (< 1 or > 65535)))
        {
            throw new ArgumentException("Host port must be between 1 and 65535.");
        }

        await EnsureSharedNetworkAsync().ConfigureAwait(false);

        var arguments = new List<string>
        {
            "run", "-d",
            "--name", $"{AppCatalog.ContainerPrefix}{app.Id}",
            "--label", $"{AppCatalog.Label}={app.Id}",
            "--network", AppCatalog.SharedNetwork,
            "--restart", "unless-stopped",
        };

        for (var index = 0; index < app.Ports.Length; index++)
        {
            var (host, container) = app.Ports[index];
            if (hostPorts is not null && index < hostPorts.Count && hostPorts[index] > 0)
            {
                host = hostPorts[index];
            }

            arguments.AddRange(["-p", $"{host}:{container}"]);
        }

        foreach (var env in envOverride ?? app.Env)
        {
            arguments.AddRange(["-e", env]);
        }

        foreach (var (volume, path) in app.Volumes)
        {
            arguments.AddRange(["-v", $"{AppCatalog.ContainerPrefix}{app.Id}-{volume}:{path}"]);
        }

        arguments.Add(app.Image);
        if (!string.IsNullOrWhiteSpace(app.Cmd))
        {
            arguments.AddRange(app.Cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Image pulls can be slow; give installs a longer leash than normal calls.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await _processRunner.RunAsync("docker", [.. arguments], workingDirectory: null, cts.Token)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Runs a command inside a running container (docker exec). The
    /// container name is validated; the command is an argv, not a shell string.</summary>
    public async Task<string> ExecAsync(string container, params string[] command)
    {
        ValidateResourceName(container);
        var arguments = new List<string> { "exec", "--", container };
        arguments.AddRange(command);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Whether a container exists and, if so, whether it is running.</summary>
    public async Task<(bool Exists, bool Running)> ContainerStateAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("inspect", "-f", "{{.State.Running}}", "--", name).ConfigureAwait(false);
        return result.Succeeded ? (true, result.StandardOutput.Trim() == "true") : (false, false);
    }

    /// <summary>
    /// Starts the managed reverse-proxy container: publishes 80/443 (TCP + UDP
    /// for HTTP/3), mounts the generated Caddyfile read-only, and keeps its ACME
    /// certificate/config in named volumes so a reinstall does not re-issue certs.
    /// </summary>
    public async Task<string> InstallProxyAsync(string container, string image, string caddyfilePath)
    {
        await EnsureSharedNetworkAsync().ConfigureAwait(false);
        string[] arguments =
        [
            "run", "-d",
            "--name", container,
            "--restart", "unless-stopped",
            "--network", AppCatalog.SharedNetwork,
            "-p", "80:80", "-p", "443:443", "-p", "443:443/udp",
            "-v", $"{caddyfilePath}:/etc/caddy/Caddyfile:ro",
            "-v", $"{container}-data:/data",
            "-v", $"{container}-config:/config",
            image,
        ];

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await _processRunner.RunAsync("docker", arguments, workingDirectory: null, cts.Token)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    private static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Copies a file out of a container to the host (docker cp).</summary>
    public async Task CopyFromContainerAsync(string container, string containerPath, string hostPath)
    {
        ValidateResourceName(container);
        var result = await RunAsync(BackupTimeout, "cp", $"{container}:{containerPath}", hostPath).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Copies a host file into a container (docker cp).</summary>
    public async Task CopyToContainerAsync(string hostPath, string container, string containerPath)
    {
        ValidateResourceName(container);
        var result = await RunAsync(BackupTimeout, "cp", hostPath, $"{container}:{containerPath}").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Tars a volume's contents into <paramref name="fileName"/> under
    /// <paramref name="hostBackupDir"/> (via a throwaway alpine container).</summary>
    public async Task BackupVolumeAsync(string volume, string hostBackupDir, string fileName)
    {
        ValidateResourceName(volume);
        string[] arguments =
        [
            "run", "--rm",
            "-v", $"{volume}:/src:ro",
            "-v", $"{hostBackupDir}:/dst",
            "alpine", "tar", "czf", $"/dst/{fileName}", "-C", "/src", ".",
        ];
        var result = await RunAsync(BackupTimeout, arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Clears a volume and extracts a snapshot tar back into it.</summary>
    public async Task RestoreVolumeAsync(string volume, string hostBackupDir, string fileName)
    {
        ValidateResourceName(volume);
        string[] arguments =
        [
            "run", "--rm",
            "-v", $"{volume}:/dst",
            "-v", $"{hostBackupDir}:/src:ro",
            "alpine", "sh", "-c", $"find /dst -mindepth 1 -delete && tar xzf /src/{fileName} -C /dst",
        ];
        var result = await RunAsync(BackupTimeout, arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    public async Task EnsureSharedNetworkAsync()
    {
        var inspect = await RunAsync("network", "inspect", AppCatalog.SharedNetwork).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            var create = await RunAsync("network", "create", AppCatalog.SharedNetwork).ConfigureAwait(false);
            // A concurrent create is fine; anything else should surface.
            if (!create.Succeeded && !create.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw Failed(create);
            }
        }
    }

    /// <summary>Removes a catalog app's container (its volumes are kept).</summary>
    public async Task<string> UninstallAppAsync(string appId)
    {
        ValidateResourceName(appId);
        var result = await RunAsync("rm", "-f", "--", $"{AppCatalog.ContainerPrefix}{appId}").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    private Task<ProcessResult> RunAsync(params string[] arguments) =>
        RunAsync(CommandTimeout, arguments);

    private async Task<ProcessResult> RunAsync(TimeSpan timeout, params string[] arguments)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _processRunner.RunAsync("docker", arguments, workingDirectory: null, cts.Token).ConfigureAwait(false);
    }

    private async Task<List<JsonElement>> JsonLinesAsync(params string[] arguments)
    {
        var result = await RunAsync(arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        return ParseJsonLinesOrArray(result.StandardOutput);
    }

    /// <summary>
    /// Docker's <c>--format json</c> output is NDJSON in some versions and a
    /// single array in others; accept both.
    /// </summary>
    internal static List<JsonElement> ParseJsonLinesOrArray(string output) => JsonLines.Parse(output);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void ValidateResourceName(string name)
    {
        // A leading '-' would let the value be parsed as a docker flag rather
        // than a positional container/network name (argument injection), so
        // reject it explicitly; the docker calls also pass '--' before the name.
        if (string.IsNullOrWhiteSpace(name)
            || name[0] is '-'
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            throw new ArgumentException($"'{name}' is not a valid container or network name.");
        }
    }

    private static InvalidOperationException Failed(ProcessResult result) =>
        new(string.IsNullOrWhiteSpace(result.StandardError)
            ? $"docker exited with code {result.ExitCode}."
            : result.StandardError.Trim());
}
