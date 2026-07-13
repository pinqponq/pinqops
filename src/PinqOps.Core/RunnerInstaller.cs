namespace PinqOps;

/// <summary>
/// Installs and registers a GitHub Actions self-hosted runner as a systemd
/// service. External work (download, extract, config.sh, svc.sh) is delegated to
/// injected abstractions so the orchestration is testable.
/// </summary>
public sealed class RunnerInstaller
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileDownloader _fileDownloader;
    private readonly Action<string>? _log;

    public RunnerInstaller(IProcessRunner processRunner, IFileDownloader fileDownloader, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _fileDownloader = fileDownloader ?? throw new ArgumentNullException(nameof(fileDownloader));
        _log = log;
    }

    /// <summary>
    /// Downloads the runner (if needed), registers it unattended, and installs +
    /// starts it as a systemd service running as <paramref name="serviceUser"/>.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> InstallAsync(
        RunnerInstallOptions options,
        string serviceUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUser);

        Directory.CreateDirectory(options.InstallDirectory);

        var configScript = Path.Combine(options.InstallDirectory, "config.sh");
        if (!File.Exists(configScript))
        {
            var archivePath = Path.Combine(options.InstallDirectory, options.ArchiveFileName);
            _log?.Invoke($"downloading runner {options.RunnerVersion}");
            await _fileDownloader.DownloadAsync(options.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);

            _log?.Invoke("extracting runner");
            if (!await RunAsync("tar", new[] { "xzf", archivePath }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        _log?.Invoke($"registering runner '{options.RunnerName}' with labels '{options.Labels}'");
        if (!await RunAsync("./config.sh", options.ConfigureArguments(), options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("installing systemd service");
        if (!await RunAsync("sudo", new[] { "./svc.sh", "install", serviceUser }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("starting systemd service");
        if (!await RunAsync("sudo", new[] { "./svc.sh", "start" }, options.InstallDirectory, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _log?.Invoke("runner installed and started");
        return true;
    }

    private async Task<bool> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(fileName, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length > 0)
        {
            _log?.Invoke(result.StandardOutput.TrimEnd());
        }

        if (!result.Succeeded)
        {
            _log?.Invoke($"'{fileName}' failed (exit {result.ExitCode}): {result.StandardError.TrimEnd()}");
            return false;
        }

        return true;
    }
}
