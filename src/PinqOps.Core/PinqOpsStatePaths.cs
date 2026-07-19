namespace PinqOps;

/// <summary>
/// Resolves the on-server pinqops state files from the compose file path — the
/// one coordinate both the CLI (on the runner) and the web dashboard already
/// share. State lives in a <c>.pinqops/</c> directory next to the compose file,
/// so it works regardless of which user runs which component.
/// </summary>
public static class PinqOpsStatePaths
{
    public const string StateDirectoryName = ".pinqops";

    /// <summary>Directory holding pinqops state, e.g. <c>/opt/pinqops/.pinqops</c>.</summary>
    public static string StateDirectory(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(composeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(composeFilePath));
        }

        return Path.Combine(directory, StateDirectoryName);
    }

    /// <summary>Deploy history JSON, e.g. <c>/opt/pinqops/.pinqops/history.json</c>.</summary>
    public static string HistoryFile(string composeFilePath) =>
        Path.Combine(StateDirectory(composeFilePath), "history.json");

    /// <summary>Notification config JSON, e.g. <c>/opt/pinqops/.pinqops/notify.json</c>.</summary>
    public static string NotifyConfigFile(string composeFilePath) =>
        Path.Combine(StateDirectory(composeFilePath), "notify.json");

    /// <summary>
    /// The compose project's dotenv file, e.g. <c>/opt/pinqops/.env</c>. Docker
    /// compose interpolates it automatically from the project directory.
    /// </summary>
    public static string EnvFile(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(composeFilePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Compose file path has no parent directory.", nameof(composeFilePath));
        }

        return Path.Combine(directory, ".env");
    }
}
