using System.Net.Http;
using System.Security.Cryptography;

namespace PinqOps;

/// <summary>
/// Replaces the running pinqops binary in place with the latest published
/// release asset, so operators can update without re-running the curl + install
/// steps from the README. The self-contained linux-x64 asset is downloaded next
/// to the current binary and atomically renamed over it; on Linux the running
/// process keeps its already-open inode, so swapping the path out is safe and
/// the new binary takes effect on the next run.
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>Where the release assets live; "latest" always redirects to the newest release.</summary>
    public const string ReleaseBaseUrl = "https://github.com/pinqponq/pinqops/releases/latest/download";

    private readonly IFileDownloader _downloader;
    private readonly Action<string> _log;

    public SelfUpdater(IFileDownloader downloader, Action<string> log)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Downloads the latest <paramref name="assetName"/> release asset and
    /// replaces the target executable with it. <paramref name="targetPath"/>
    /// defaults to the running binary (<see cref="Environment.ProcessPath"/>);
    /// tests pass an explicit path. Returns the path that was replaced on
    /// success, or null on a handled failure (already logged).
    /// </summary>
    public async Task<string?> UpdateAsync(string assetName, string? targetPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        // Only linux-x64 self-contained binaries are published, so a swap on any
        // other OS would install something that can't run here.
        if (!OperatingSystem.IsLinux())
        {
            _log("error: self-update is only supported on linux-x64 (the only published binary).");
            return null;
        }

        var target = targetPath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log("error: could not determine the path of the running binary.");
            return null;
        }

        // Guard the 'dotnet run' case only when updating the running binary — an
        // explicit target is a real file path the caller chose.
        if (targetPath is null && Path.GetFileName(target) is "dotnet" or "dotnet.exe")
        {
            _log("error: run 'update' from the installed binary, not via 'dotnet run'.");
            return null;
        }

        target = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(target)!;
        var url = $"{ReleaseBaseUrl}/{assetName}";

        // Download beside the target so the final rename stays on one filesystem
        // (rename is only atomic within a volume) and a half-finished download can
        // never clobber the working binary.
        var temp = Path.Combine(directory, $".{assetName}.update-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");

        _log($"downloading {url}");
        try
        {
            await _downloader.DownloadAsync(url, temp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _log($"error: download failed: {exception.Message}");
            if (exception is UnauthorizedAccessException)
            {
                _log($"cannot write to {directory} — re-run with sudo.");
            }

            TryDelete(temp);
            return null;
        }

        try
        {
            var downloaded = new FileInfo(temp);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                _log("error: the downloaded binary is missing or empty; keeping the current one.");
                TryDelete(temp);
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                // rwxr-xr-x — the same mode the install steps set with chmod +x.
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Atomic replace: the running process holds the old inode open, so
            // swapping the path out from under it is safe.
            File.Move(temp, target, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            _log($"error: cannot replace {target} — re-run with sudo.");
            TryDelete(temp);
            return null;
        }
        catch (IOException exception)
        {
            _log($"error: could not replace {target}: {exception.Message}");
            TryDelete(temp);
            return null;
        }

        _log($"updated {target}");
        return target;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the leftover temp file is harmless and hidden (dot-prefixed).
            _log($"note: could not clean up {path}: {exception.Message}");
        }
    }
}
