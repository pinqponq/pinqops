namespace PinqOps;

/// <summary>
/// Runs the fixed deploy sequence against a single, predefined compose project:
/// pull the new image, recreate the containers, then (best effort) prune old
/// images. The process runner is injected so the sequence is unit-testable
/// without Docker.
/// </summary>
public sealed class Deployer
{
    private const string DockerExecutable = "docker";

    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;

    public Deployer(IProcessRunner processRunner, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
    }

    /// <summary>
    /// Runs <c>pull</c> then <c>up -d</c> (and optional <c>image prune</c>).
    /// Returns true only if pull and up both succeed.
    /// </summary>
    public async Task<bool> DeployAsync(DeployOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        if (!await RunStepAsync(DockerComposeCommandBuilder.Pull(options.ComposeFilePath), token).ConfigureAwait(false))
        {
            return false;
        }

        if (!await RunStepAsync(DockerComposeCommandBuilder.Up(options.ComposeFilePath), token).ConfigureAwait(false))
        {
            return false;
        }

        if (options.PruneImages)
        {
            // Pruning is a cleanup nicety; its failure must not fail the deploy.
            await RunStepAsync(DockerComposeCommandBuilder.PruneImages(), token).ConfigureAwait(false);
        }

        _log?.Invoke("deploy succeeded");
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
