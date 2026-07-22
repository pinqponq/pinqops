using PinqOps.Proxy;

namespace PinqOps;

/// <summary>
/// Manages per-PR preview environments on the server. A preview is a second,
/// throwaway compose project (<c>&lt;repo&gt;-pr-&lt;n&gt;</c>) that runs the image built
/// for a pull request on its own free host port, next to production. It reuses
/// production's <c>.env</c> — minus the pinned image/tag/host-port — so previews
/// behave like prod without a separate setup.
/// </summary>
/// <remarks>
/// Runs on the runner (invoked by <c>pinqops preview deploy|teardown</c> from the
/// PR workflow), so it talks to Docker and the proxy directly and never depends
/// on the dashboard being reachable — the "no inbound port" model holds.
/// </remarks>
public sealed class PreviewManager
{
    private const string DockerExecutable = "docker";

    /// <summary>Preview host ports start here; the PR number spreads them out before the free-port scan.</summary>
    public const int HostPortBase = 9100;

    /// <summary>Keys re-pinned per environment — never copied from prod's .env into a preview.</summary>
    private static readonly string[] NotCopiedFromProd =
    [
        Deployer.TagVariable,
        Deployer.ImageVariable,
        Deployer.HostPortVariable,
    ];

    private readonly IProcessRunner _runner;
    private readonly string _proxyDirectory;
    private readonly Action<string>? _log;

    public PreviewManager(IProcessRunner runner, string? proxyDirectory = null, Action<string>? log = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _proxyDirectory = proxyDirectory ?? ProxyPaths.DefaultDirectory;
        _log = log;
    }

    /// <summary>The directory holding all of an app's previews, e.g. <c>/opt/pinqops/apps/x/previews</c>.</summary>
    public static string PreviewsRoot(string prodComposeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prodComposeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(prodComposeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(prodComposeFilePath));
        }

        return Path.Combine(directory, "previews");
    }

    /// <summary>One preview's directory, <c>&lt;previews&gt;/pr-&lt;n&gt;</c>.</summary>
    public static string PreviewDirectory(string prodComposeFilePath, int pullRequestNumber)
    {
        RequireValidPr(pullRequestNumber);
        return Path.Combine(PreviewsRoot(prodComposeFilePath), $"pr-{pullRequestNumber}");
    }

    /// <summary>The preview's compose file path.</summary>
    public static string PreviewComposeFile(string prodComposeFilePath, int pullRequestNumber) =>
        Path.Combine(PreviewDirectory(prodComposeFilePath, pullRequestNumber), "docker-compose.yml");

    /// <summary>The preview's compose project name, <c>&lt;repo&gt;-pr-&lt;n&gt;</c>.</summary>
    public static string PreviewProjectName(string repo, int pullRequestNumber)
    {
        RequireValidPr(pullRequestNumber);
        return $"{ComposeProjectName.FromRepository(repo)}-pr-{pullRequestNumber}";
    }

    /// <summary>The container name compose creates for the preview, <c>&lt;repo&gt;-pr-&lt;n&gt;-app-1</c>.</summary>
    public static string PreviewContainerName(string repo, int pullRequestNumber) =>
        $"{PreviewProjectName(repo, pullRequestNumber)}-app-1";

    /// <summary>Every preview currently on disk for this app, newest PR first.</summary>
    public static IReadOnlyList<PreviewInfo> List(string prodComposeFilePath, string repo)
    {
        var root = PreviewsRoot(prodComposeFilePath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var previews = new List<PreviewInfo>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("pr-", StringComparison.Ordinal)
                || !int.TryParse(name["pr-".Length..], out var pr)
                || pr <= 0)
            {
                continue;
            }

            var composeFile = Path.Combine(directory, "docker-compose.yml");
            var envFile = Path.Combine(directory, ".env");
            var hostPort = ParsePort(EnvFileStore.GetValue(envFile, Deployer.HostPortVariable));
            previews.Add(new PreviewInfo(pr, PreviewProjectName(repo, pr), composeFile, hostPort));
        }

        return previews.OrderByDescending(preview => preview.PullRequestNumber).ToList();
    }

    /// <summary>
    /// Creates or updates the preview for a PR: writes its compose file and a
    /// prod-derived <c>.env</c>, pulls the built image, brings it up, and (when the
    /// app has a domain) routes <c>pr-&lt;n&gt;.&lt;domain&gt;</c> to it. Idempotent — a second
    /// deploy of the same PR just re-pins the new image and restarts.
    /// </summary>
    public async Task<PreviewDeployResult> DeployAsync(PreviewDeployRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireValidPr(request.PullRequestNumber);

        var alreadyDeployed = Directory.Exists(PreviewDirectory(request.ProdComposeFilePath, request.PullRequestNumber));
        if (!alreadyDeployed)
        {
            var max = new PreviewConfigStore(request.ProdComposeFilePath).Load().MaxPreviews;
            var running = List(request.ProdComposeFilePath, request.Repo).Count;
            if (running >= max)
            {
                var error = $"preview limit reached ({running}/{max}). Tear down an old preview or raise MaxPreviews.";
                _log?.Invoke(error);
                return new PreviewDeployResult(false, 0, null, error);
            }
        }

        // Prod's container port carries over (the app listens on the same port in
        // every environment); everything else about ports/images is per-preview.
        var prodEnv = PinqOpsStatePaths.EnvFile(request.ProdComposeFilePath);
        var containerPort = ParsePort(EnvFileStore.GetValue(prodEnv, Deployer.ContainerPortVariable)) ?? 8080;

        var hostPort = HostPort.FindAvailable(HostPortBase + request.PullRequestNumber % 400);
        if (hostPort is null)
        {
            var error = "no free host port for the preview — every candidate port is taken.";
            _log?.Invoke(error);
            return new PreviewDeployResult(false, 0, null, error);
        }

        var directory = PreviewDirectory(request.ProdComposeFilePath, request.PullRequestNumber);
        Directory.CreateDirectory(directory);

        var composeFile = PreviewComposeFile(request.ProdComposeFilePath, request.PullRequestNumber);
        var projectName = PreviewProjectName(request.Repo, request.PullRequestNumber);
        File.WriteAllText(composeFile, ComposeTemplate.Yaml(request.Owner, request.Repo, projectName, hostPort.Value, containerPort));

        WritePreviewEnv(prodEnv, PinqOpsStatePaths.EnvFile(composeFile), request.Image, request.Tag, hostPort.Value);

        var pullFailure = await RunStepAsync(DockerComposeCommandBuilder.Pull(composeFile), cancellationToken).ConfigureAwait(false);
        if (pullFailure is not null)
        {
            return new PreviewDeployResult(false, hostPort.Value, null, $"image pull failed: {pullFailure}");
        }

        var upFailure = await RunStepAsync(DockerComposeCommandBuilder.Up(composeFile), cancellationToken).ConfigureAwait(false);
        if (upFailure is not null)
        {
            return new PreviewDeployResult(false, hostPort.Value, null, $"compose up failed: {upFailure}");
        }

        var url = await RegisterPreviewDomainAsync(request, containerPort, cancellationToken).ConfigureAwait(false);
        _log?.Invoke(url is not null
            ? $"preview for PR #{request.PullRequestNumber} is up at {url} (and http://<server>:{hostPort.Value})"
            : $"preview for PR #{request.PullRequestNumber} is up at http://<server>:{hostPort.Value}");
        return new PreviewDeployResult(true, hostPort.Value, url, null);
    }

    /// <summary>
    /// Removes a PR's preview: brings the project down with its volumes, deletes
    /// the directory, and drops its proxy route. Idempotent — tearing down a
    /// preview that was never created (or was already removed) is a no-op success.
    /// </summary>
    public async Task<bool> TeardownAsync(string prodComposeFilePath, string repo, int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        RequireValidPr(pullRequestNumber);

        var composeFile = PreviewComposeFile(prodComposeFilePath, pullRequestNumber);
        if (File.Exists(composeFile))
        {
            // down -v: a preview's data is throwaway, so its volumes go with it.
            await RunStepAsync(new[] { "compose", "-f", composeFile, "down", "-v" }, cancellationToken).ConfigureAwait(false);
        }

        var directory = PreviewDirectory(prodComposeFilePath, pullRequestNumber);
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"could not delete {directory}: {exception.Message}");
            }
        }

        await RemovePreviewDomainAsync(repo, pullRequestNumber, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"preview for PR #{pullRequestNumber} torn down");
        return true;
    }

    /// <summary>
    /// Builds the preview's <c>.env</c>: every prod assignment except the three
    /// per-environment keys, then the preview's own image, tag and host port. App
    /// secrets are copied deliberately so the preview behaves like prod.
    /// </summary>
    private static void WritePreviewEnv(string prodEnv, string previewEnv, string image, string tag, int hostPort)
    {
        foreach (var (key, value) in EnvFileStore.GetAll(prodEnv))
        {
            if (!NotCopiedFromProd.Contains(key))
            {
                EnvFileStore.SetValue(previewEnv, key, value);
            }
        }

        EnvFileStore.SetValue(previewEnv, Deployer.ImageVariable, image);
        EnvFileStore.SetValue(previewEnv, Deployer.TagVariable, tag);
        EnvFileStore.SetValue(previewEnv, Deployer.HostPortVariable, hostPort.ToString());
    }

    /// <summary>
    /// When the app has a domain in the shared proxy config, adds
    /// <c>pr-&lt;n&gt;.&lt;domain&gt;</c> pointing at the preview container and reloads Caddy.
    /// Best-effort: a missing proxy, or a reload failure, never fails the deploy —
    /// the preview is still reachable on its host port. Returns the preview URL, or
    /// null when no domain was routed.
    /// </summary>
    private async Task<string?> RegisterPreviewDomainAsync(PreviewDeployRequest request, int containerPort, CancellationToken cancellationToken)
    {
        try
        {
            var store = new DomainConfigStore(_proxyDirectory);
            if (!File.Exists(store.Path_))
            {
                return null;
            }

            var prodContainer = $"{ComposeProjectName.FromRepository(request.Repo)}-app-1";
            var config = store.Load();
            var baseDomains = config.Domains
                .Where(entry => entry.Enabled
                    && !IsPreviewMarker(entry.Target)
                    && string.Equals(entry.TargetContainer, prodContainer, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (baseDomains.Count == 0)
            {
                return null;
            }

            var previewContainer = PreviewContainerName(request.Repo, request.PullRequestNumber);
            var marker = PreviewMarker(request.Repo, request.PullRequestNumber);
            string? firstUrl = null;
            foreach (var baseDomain in baseDomains)
            {
                var previewDomain = $"pr-{request.PullRequestNumber}.{baseDomain}";
                config.Domains.RemoveAll(entry => string.Equals(entry.Domain, previewDomain, StringComparison.OrdinalIgnoreCase));
                config.Domains.Add(new DomainEntry
                {
                    Domain = previewDomain,
                    Target = marker,
                    TargetContainer = previewContainer,
                    TargetPort = containerPort,
                    Enabled = true,
                    CreatedAt = request.Now,
                });
                firstUrl ??= $"https://{previewDomain}";
            }

            store.Save(config);
            await ReloadProxyAsync(cancellationToken).ConfigureAwait(false);
            return firstUrl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not register preview domain: {exception.Message}");
            return null;
        }
    }

    private async Task RemovePreviewDomainAsync(string repo, int pullRequestNumber, CancellationToken cancellationToken)
    {
        try
        {
            var store = new DomainConfigStore(_proxyDirectory);
            if (!File.Exists(store.Path_))
            {
                return;
            }

            var config = store.Load();
            var marker = PreviewMarker(repo, pullRequestNumber);
            var removed = config.Domains.RemoveAll(entry => string.Equals(entry.Target, marker, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return;
            }

            store.Save(config);
            await ReloadProxyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not remove preview domain: {exception.Message}");
        }
    }

    private async Task ReloadProxyAsync(CancellationToken cancellationToken)
    {
        // Same command the dashboard's ProxyService.ApplyAsync uses — a hot reload
        // with no downtime for the other domains.
        await _runner.RunAsync(
            DockerExecutable,
            new[] { "exec", ProxyContainerName, "caddy", "reload", "--config", "/etc/caddy/Caddyfile" },
            workingDirectory: null,
            cancellationToken).ConfigureAwait(false);
    }

    private const string ProxyContainerName = "pinqops-proxy";

    /// <summary>The <see cref="DomainEntry.Target"/> value marking a preview route, so teardown can find its own.</summary>
    public static string PreviewMarker(string repo, int pullRequestNumber) =>
        $"preview:{ComposeProjectName.FromRepository(repo)}:{pullRequestNumber}";

    private static bool IsPreviewMarker(string? target) =>
        target is not null && target.StartsWith("preview:", StringComparison.Ordinal);

    private async Task<string?> RunStepAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _log?.Invoke($"$ {DockerExecutable} {string.Join(' ', arguments)}");
        var result = await _runner.RunAsync(DockerExecutable, arguments, workingDirectory: null, cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (result.Succeeded)
        {
            return null;
        }

        var reason = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? $"exit {result.ExitCode}";
        _log?.Invoke($"command failed (exit {result.ExitCode}): {reason}");
        return reason;
    }

    private static int? ParsePort(string? raw) =>
        int.TryParse(raw, out var port) && HostPort.IsValid(port) ? port : null;

    private static void RequireValidPr(int pullRequestNumber)
    {
        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestNumber), "Pull request number must be positive.");
        }
    }
}

/// <summary>Everything needed to bring up a PR preview.</summary>
public sealed record PreviewDeployRequest(
    string ProdComposeFilePath,
    string Owner,
    string Repo,
    int PullRequestNumber,
    string Image,
    string Tag,
    DateTimeOffset Now);

/// <summary>Outcome of a preview deploy.</summary>
public sealed record PreviewDeployResult(bool Succeeded, int HostPort, string? Url, string? Error);

/// <summary>A preview that exists on disk.</summary>
public sealed record PreviewInfo(int PullRequestNumber, string ProjectName, string ComposeFilePath, int? HostPort);
