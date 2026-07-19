using System.Security.Cryptography;

namespace PinqOps;

/// <summary>
/// Writes a file atomically: the bytes go to a sibling temp file that is then
/// renamed over the target, so a crash mid-write can never leave a truncated
/// file (which for the config/credential stores would silently reset the
/// dashboard to its unauthenticated setup state). When <paramref name="ownerOnly"/>
/// is set the temp file is created 0600 <em>before</em> any content is written,
/// so secret bytes (a PAT, generated app passwords) never touch a
/// world-readable inode — closing the create-then-chmod TOCTOU window.
/// </summary>
public static class SecureFile
{
    public static void WriteAllText(string path, string contents, bool ownerOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.tmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
            }

            // Rename is atomic on the same volume; the destination inherits the
            // temp file's owner-only mode.
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best effort cleanup; surface the original failure.
            }

            throw;
        }
    }
}
