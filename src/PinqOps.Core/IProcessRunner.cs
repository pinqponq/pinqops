namespace PinqOps;

/// <summary>
/// Runs an external process. Abstracted so the deploy/install logic can be
/// unit-tested without invoking real binaries such as docker.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given argument list. Arguments
    /// are passed as discrete items (never a concatenated shell string), so no
    /// value can inject additional commands.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}
