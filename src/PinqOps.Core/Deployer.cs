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
        var healthState = DeployRecordValues.HealthSkipped;

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

        if (pullNeeded
            && !await RunStepAsync(DockerComposeCommandBuilder.Pull(options.ComposeFilePath), token).ConfigureAwait(false))
        {
            var error = options.Trigger == DeployRecordValues.TriggerRollback
                ? "image pull failed — the rollback target is no longer local and pulling from the registry "
                  + "requires docker login (e.g. a token with read:packages)"
                : "image pull failed";

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

        if (!await RunStepAsync(DockerComposeCommandBuilder.Up(options.ComposeFilePath), token).ConfigureAwait(false))
        {
            await FinishAsync(options, startedAt, DeployRecordValues.ResultFailed, previousTag, healthState, "compose up failed", cancellationToken)
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

    private async Task<bool> RunStepAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _log?.Invoke($"$ {DockerExecutable} {string.Join(' ', arguments)}");

        var result = await _processRunner
            .RunAsync(DockerExecutable, arguments, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (!result.Succeeded)
        {
            _log?.Invoke($"command failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}");
            return false;
        }

        return true;
    }
}
