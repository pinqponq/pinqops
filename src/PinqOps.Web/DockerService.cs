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
        var result = await RunAsync("network", "inspect", name).ConfigureAwait(false);
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

        arguments.Add(name);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> RemoveNetworkAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("network", "rm", name).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> ConnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "connect", network, container).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> DisconnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "disconnect", network, container).ConfigureAwait(false);
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
        var result = await RunAsync("logs", "--tail", tail.ToString(), "--timestamps", containerId).ConfigureAwait(false);
        // Docker writes app output to both streams; show them together like the terminal does.
        return result.Succeeded || result.StandardError.Length > 0 || result.StandardOutput.Length > 0
            ? result.StandardOutput + result.StandardError
            : throw Failed(result);
    }

    public async Task<JsonElement> InspectContainerAsync(string containerId)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("inspect", containerId).ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : throw Failed(result);
    }

    public async Task<string> ContainerActionAsync(string containerId, string action)
    {
        ValidateResourceName(containerId);
        if (!AllowedContainerActions.Contains(action))
        {
            throw new ArgumentException($"Unsupported container action '{action}'.");
        }

        var result = await RunAsync(action, containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> PruneImagesAsync()
    {
        var result = await RunAsync("image", "prune", "-f").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Runs a catalog app as a labeled, named container on the shared
    /// pinqops-apps network. Each entry in <paramref name="hostPorts"/>
    /// overrides the corresponding catalog port (0/absent keeps the default).
    /// </summary>
    public async Task<string> InstallAppAsync(AppSpec app, IReadOnlyList<int>? hostPorts)
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

        foreach (var env in app.Env)
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

    private async Task EnsureSharedNetworkAsync()
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
        var result = await RunAsync("rm", "-f", $"{AppCatalog.ContainerPrefix}{appId}").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// <c>docker login</c> with the password on stdin so the token never appears
    /// in an argument list or process table.
    /// </summary>
    public async Task<string> LoginAsync(string registry, string username, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "login", registry, "-u", username, "--password-stdin" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'docker login'.");
        await process.StandardInput.WriteAsync(token).ConfigureAwait(false);
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(CommandTimeout);
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        return process.ExitCode == 0
            ? stdout.Trim()
            : throw new InvalidOperationException($"docker login failed: {stderr.Trim()}");
    }

    private async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        using var cts = new CancellationTokenSource(CommandTimeout);
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
    internal static List<JsonElement> ParseJsonLinesOrArray(string output)
    {
        var items = new List<JsonElement>();
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return items;
        }

        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            items.AddRange(document.RootElement.EnumerateArray().Select(element => element.Clone()));
            return items;
        }

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            items.Add(document.RootElement.Clone());
        }

        return items;
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void ValidateResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
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
