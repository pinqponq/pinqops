using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Runs the fixed deploy sequence against a single, predefined compose project:
/// pin the requested tag in the project's <c>.env</c>, pull, recreate the
/// containers, verify they come up healthy, then record the outcome and keep a
/// bounded set of images for rollback. The process runner is injected so the
/// sequence is unit-testable without Docker.
/// </summary>
public sealed class Deployer
{
    private const string DockerExecutable = "docker";
    public const string TagVariable = "PINQOPS_TAG";
    public const string ImageVariable = "PINQOPS_IMAGE";

    /// <summary>Host port the compose project publishes on (user-editable).</summary>
    public const string HostPortVariable = "PINQOPS_HOST_PORT";

    /// <summary>Port inside the container the app listens on (user-editable).</summary>
    public const string ContainerPortVariable = "PINQOPS_CONTAINER_PORT";

    /// <summary>
    /// Whether a compose <c>.env</c> key is owned by deploy/rollback. Both are
    /// re-pinned on every deploy, so editing one by hand silently disappears —
    /// callers surface them as read-only rather than accept the edit.
    /// </summary>
    public static bool IsDeployManagedVariable(string key) =>
        key == TagVariable || key == ImageVariable;

    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;
    private readonly DeployHistoryStore? _history;
    private readonly IDeployObserver? _observer;
    private readonly ComposeHealthChecker _healthChecker;
    private readonly ImageRetentionPruner _retentionPruner;

    public Deployer(
        IProcessRunner processRunner,
        Action<string>? log = null,
        DeployHistoryStore? history = null,
        IDeployObserver? observer = null,
        ComposeHealthChecker? healthChecker = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
        _history = history;
        _observer = observer;
        _healthChecker = healthChecker ?? new ComposeHealthChecker(processRunner, log);
        _retentionPruner = new ImageRetentionPruner(processRunner, log);
    }

    /// <summary>
    /// Runs the deploy sequence. Returns true only when pull, up and the health
    /// check (when enabled) all succeed. There is no automatic rollback: a
    /// failed deploy is recorded and reported, and rolling back is an explicit
    /// user action.
    /// </summary>
    public async Task<bool> DeployAsync(DeployOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        var startedAt = DateTimeOffset.UtcNow;
        var envFile = PinqOpsStatePaths.EnvFile(options.ComposeFilePath);
        var previousTag = EnvFileStore.GetValue(envFile, TagVariable);
        var previousImage = EnvFileStore.GetValue(envFile, ImageVariable);
        var healthState = DeployRecordValues.HealthSkipped;

        if (options.ExpectedImage is not null)
        {
            // BEFORE touching the .env: does this project even belong to us? The
            // project name is the owning repository's, and it is the only durable
            // marker — pinning the image first would make every later check pass
            // while quietly hijacking another application's project.
            var wrongProject = FindProjectOwnerMismatch(options.ExpectedImage, options.ComposeFilePath);
            if (wrongProject is not null)
            {
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, healthState, wrongProject, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            // Pin the image so the compose resolves to what this deploy is for; a
            // repository rename then flows straight through the workflow's
            // --image with no stale compose file to fix by hand.
            EnvFileStore.SetValue(envFile, ImageVariable, options.ExpectedImage);
            _log?.Invoke($"pinned {ImageVariable}={options.ExpectedImage}");

            var mismatch = await FindImageMismatchAsync(options.ExpectedImage, options.ComposeFilePath, token)
                .ConfigureAwait(false);
            if (mismatch is not null)
            {
                // Nothing was applied; leave the .env describing what is running.
                RestoreEnvValue(envFile, ImageVariable, previousImage);
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, healthState, mismatch, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
        }

        if (options.Tag is not null)
        {
            EnvFileStore.SetValue(envFile, TagVariable, options.Tag);
            _log?.Invoke($"pinned {TagVariable}={options.Tag}");
        }

        var pullNeeded = true;
        if (options.Trigger == DeployRecordValues.TriggerRollback)
        {
            pullNeeded = !await ImagesPresentLocallyAsync(options.ComposeFilePath, token).ConfigureAwait(false);
            if (!pullNeeded)
            {
                _log?.Invoke("rollback target image found locally; skipping pull");
            }
        }

        var pullFailure = pullNeeded
            ? await RunStepAsync(DockerComposeCommandBuilder.Pull(options.ComposeFilePath), token).ConfigureAwait(false)
            : null;
        if (pullFailure is not null)
        {
            var error = options.Trigger == DeployRecordValues.TriggerRollback
                ? $"image pull failed ({pullFailure}) — the rollback target is no longer local and pulling from the "
                  + "registry requires docker login (e.g. a token with read:packages)"
                : $"image pull failed: {pullFailure}";

            // Nothing was applied; restore the previously pinned tag so the
            // .env keeps describing what is actually running.
            if (options.Tag is not null && previousTag is not null && previousTag != options.Tag)
            {
                EnvFileStore.SetValue(envFile, TagVariable, previousTag);
            }

            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, healthState, error, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var upFailure = await RunStepAsync(DockerComposeCommandBuilder.Up(options.ComposeFilePath), token)
            .ConfigureAwait(false);
        if (upFailure is not null)
        {
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, healthState, $"compose up failed: {upFailure}", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (options.HealthCheckTimeout > TimeSpan.Zero)
        {
            var failure = await _healthChecker
                .WaitForHealthyAsync(options.ComposeFilePath, options.HealthCheckTimeout, token)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                _log?.Invoke($"health check failed: {failure}");
                await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, DeployRecordValues.HealthFailed, failure, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            healthState = DeployRecordValues.HealthPassed;
        }

        await WarnOnUnservedPortAsync(options.ComposeFilePath, token).ConfigureAwait(false);

        if (options.PruneImages)
        {
            // Cleanup is a nicety; its failure must not fail the deploy.
            await _retentionPruner.PruneAsync(options.ComposeFilePath, options.KeepImages, token).ConfigureAwait(false);
        }

        var result = options.Trigger == DeployRecordValues.TriggerRollback
            ? DeployRecordValues.ResultRolledBack
            : DeployRecordValues.ResultSucceeded;
        await FinishAsync(options, startedAt, result, previousTag, healthState, error: null, cancellationToken)
            .ConfigureAwait(false);
        _log?.Invoke(options.Trigger == DeployRecordValues.TriggerRollback ? "rollback succeeded" : "deploy succeeded");
        return true;
    }

    private async Task FinishAsync(
        DeployOptions options,
        DateTimeOffset startedAt,
        string result,
        string? previousTag,
        string healthState,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            _history?.Append(new DeployRecord
            {
                Id = DeployHistoryStore.NewRecordId(),
                Tag = options.Tag ?? "latest",
                StartedAt = startedAt,
                DurationSeconds = Math.Round((DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1),
                Result = result,
                Trigger = options.Trigger,
                PreviousTag = previousTag,
                HealthCheck = healthState,
                Error = error,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"could not write deploy history: {exception.Message}");
        }

        if (_observer is not null)
        {
            try
            {
                await _observer.OnDeployCompletedAsync(
                    new DeployOutcome
                    {
                        Result = result,
                        Trigger = options.Trigger,
                        Tag = options.Tag ?? "latest",
                        PreviousTag = previousTag,
                        HealthCheck = healthState,
                        Error = error,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log?.Invoke($"deploy observer failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Verifies the compose project actually references <paramref name="expectedImage"/>
    /// — it will unless the image line was hand-edited to hardcode a name instead of
    /// using the pinned variable. Returns an actionable error message when it does not,
    /// or null when it matches or the reference set could not be read (the pull would
    /// surface that anyway).
    /// </summary>
    private async Task<string?> FindImageMismatchAsync(string expectedImage, string composeFilePath, CancellationToken cancellationToken)
    {
        var imagesResult = await _processRunner
            .RunAsync(DockerExecutable, DockerComposeCommandBuilder.ConfigImages(composeFilePath), workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            // Can't read the reference set (e.g. a compose file pinqops did not
            // generate); don't invent a failure — let pull report any real error.
            _log?.Invoke($"could not read compose images to verify the target ({imagesResult.StandardError.TrimEnd()}); skipping the check");
            return null;
        }

        var repositories = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ImageReference.RepositoryOf)
            .ToArray();

        if (repositories.Any(repository => string.Equals(repository, expectedImage, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var referenced = repositories.Length > 0 ? string.Join(", ", repositories) : "(none)";
        _log?.Invoke(
            $"image mismatch: this deploy is for {expectedImage} but {composeFilePath} references {referenced}");
        return $"compose file targets the wrong image. Expected {expectedImage}, but {composeFilePath} "
            + $"references {referenced} — its image: line hardcodes a name instead of using the pinned variable. "
            + $"Set it to ${{{ImageVariable}:-{expectedImage}}}:${{{TagVariable}:-latest}} so the image follows the "
            + $"repository, then redeploy.";
    }

    private static void RestoreEnvValue(string envFile, string key, string? previousValue)
    {
        if (previousValue is null)
        {
            EnvFileStore.RemoveValue(envFile, key);
        }
        else
        {
            EnvFileStore.SetValue(envFile, key, previousValue);
        }
    }

    /// <summary>
    /// Returns an error when the compose project belongs to a different
    /// application than the one being deployed, or null when it matches (or the
    /// file declares no project name, e.g. a hand-written one).
    /// </summary>
    /// <remarks>
    /// pinqops manages one application per compose file. Pointing a second
    /// repository at the same path is silently destructive: its deploy pins its
    /// own image and tag over the first application's, so the wrong image runs
    /// under the wrong project — or the pull dies on a tag that only exists in
    /// the other package. The project name is the repository's, so comparing it
    /// against the image being deployed catches this before anything is written.
    /// </remarks>
    private static string? FindProjectOwnerMismatch(string expectedImage, string composeFilePath)
    {
        if (!File.Exists(composeFilePath))
        {
            return null;
        }

        string? declaredProject;
        try
        {
            declaredProject = ComposeProjectName.ReadFrom(File.ReadAllText(composeFilePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (declaredProject is null)
        {
            return null;
        }

        // ghcr.io/<owner>/<repo> — the last segment is the repository, which is
        // what the project name is derived from.
        var repositorySegment = expectedImage[(expectedImage.LastIndexOf('/') + 1)..];
        if (repositorySegment.Length == 0)
        {
            return null;
        }

        var expectedProject = ComposeProjectName.FromRepository(repositorySegment);
        if (string.Equals(declaredProject, expectedProject, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{composeFilePath} is the compose project of '{declaredProject}', but this deploy is for "
            + $"'{expectedProject}'. pinqops manages one application per compose file — give this one its own "
            + $"path (e.g. /opt/{expectedProject}/docker-compose.yml) and set the repository variable "
            + $"APP_COMPOSE_PATH to it, or the two will overwrite each other.";
    }

    /// <summary>
    /// Warns when the project publishes a container port the image does not
    /// expose. The container still runs and the health check still passes, so
    /// without this the deploy is reported green while nothing answers on the
    /// published port — the classic "it deployed but the site is dead".
    /// </summary>
    /// <remarks>
    /// Advisory only. <c>EXPOSE</c> is documentation: an app may legitimately
    /// listen on a port its image never declared, so this must never fail a
    /// deploy — it only says what looks wrong.
    /// </remarks>
    private async Task WarnOnUnservedPortAsync(string composeFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var images = await RunCapturedAsync(DockerComposeCommandBuilder.ConfigImages(composeFilePath), cancellationToken)
                .ConfigureAwait(false);
            var image = images?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (image is null)
            {
                return;
            }

            var exposedJson = await RunCapturedAsync(DockerComposeCommandBuilder.InspectImageExposedPorts(image), cancellationToken)
                .ConfigureAwait(false);
            var exposed = ParseExposedPorts(exposedJson);
            if (exposed.Count == 0)
            {
                // Nothing declared — no basis for an opinion.
                return;
            }

            var psOutput = await RunCapturedAsync(DockerComposeCommandBuilder.Ps(composeFilePath), cancellationToken)
                .ConfigureAwait(false);
            if (psOutput is null)
            {
                return;
            }

            foreach (var service in JsonLines.Parse(psOutput))
            {
                if (!service.TryGetProperty("Publishers", out var publishers)
                    || publishers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var publisher in publishers.EnumerateArray())
                {
                    if (!publisher.TryGetProperty("TargetPort", out var targetPort)
                        || !targetPort.TryGetInt32(out var target)
                        || target == 0
                        || exposed.Contains(target))
                    {
                        continue;
                    }

                    _log?.Invoke(
                        $"warning: publishing to container port {target}, but the image only exposes "
                        + $"{string.Join(", ", exposed)}. If nothing is listening on {target} the app will be "
                        + $"unreachable — set {ContainerPortVariable} in the project's .env to the port your app "
                        + $"listens on, then re-apply.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A diagnostic must never break a deploy that already succeeded.
            _log?.Invoke($"could not check the published port: {exception.Message}");
        }
    }

    /// <summary>Port numbers from a docker <c>ExposedPorts</c> map (<c>{"8083/tcp":{}}</c>).</summary>
    private static HashSet<int> ParseExposedPorts(string? exposedPortsJson)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(exposedPortsJson))
        {
            return ports;
        }

        try
        {
            using var document = JsonDocument.Parse(exposedPortsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ports;
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var slash = entry.Name.IndexOf('/');
                var portText = slash >= 0 ? entry.Name[..slash] : entry.Name;
                if (int.TryParse(portText, out var port))
                {
                    ports.Add(port);
                }
            }
        }
        catch (JsonException)
        {
            // "null" or an unexpected shape — treat as "nothing declared".
        }

        return ports;
    }

    /// <summary>Standard output of a docker command, or null when it failed.</summary>
    private async Task<string?> RunCapturedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(DockerExecutable, arguments, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : null;
    }

    /// <summary>True when every image the compose project references exists locally.</summary>
    private async Task<bool> ImagesPresentLocallyAsync(string composeFilePath, CancellationToken cancellationToken)
    {
        var imagesResult = await _processRunner
            .RunAsync(DockerExecutable, DockerComposeCommandBuilder.ConfigImages(composeFilePath), workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            return false;
        }

        var references = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (references.Length == 0)
        {
            return false;
        }

        foreach (var reference in references)
        {
            var inspect = await _processRunner
                .RunAsync(DockerExecutable, DockerComposeCommandBuilder.InspectImage(reference), workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            if (!inspect.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs a docker step. Returns null on success, otherwise docker's own
    /// reason — which the caller puts in the deploy record and the notification,
    /// so "port is already allocated" reaches Slack instead of a bare
    /// "compose up failed".
    /// </summary>
    private async Task<string?> RunStepAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _log?.Invoke($"$ {DockerExecutable} {string.Join(' ', arguments)}");

        var result = await _processRunner
            .RunAsync(DockerExecutable, arguments, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (result.Succeeded)
        {
            return null;
        }

        _log?.Invoke($"command failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}");
        return Condense(result.StandardError) ?? $"exit {result.ExitCode}";
    }

    /// <summary>
    /// The most specific line of a docker error, capped so it stays readable in
    /// a chat notification. Docker prints the actual cause last.
    /// </summary>
    private static string? Condense(string standardError)
    {
        const int MaxLength = 300;

        var lastLine = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (lastLine is null)
        {
            return null;
        }

        return lastLine.Length <= MaxLength ? lastLine : lastLine[..MaxLength] + "…";
    }
}
