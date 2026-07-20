namespace PinqOps.Web;

/// <summary>
/// Persisted state of the web UI: the dashboard password hash, the GitHub
/// connection, and the paths the dashboard inspects. Stored as JSON with 0600
/// permissions (it contains the PAT).
/// </summary>
public sealed class UiConfig
{
    public const string DefaultComposeFile = "/opt/pinqops/docker-compose.yml";
    public const string DefaultRunnerDirectory = "/opt/actions-runner";

    /// <summary>
    /// Host port a newly generated compose project publishes the app on. Kept off
    /// 80/443 so the first deploy cannot collide with something already bound.
    /// </summary>
    public const int DefaultHostPort = 8080;

    /// <summary>PBKDF2 hash of the dashboard password ("salt.hash" base64).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>The GitHub repository URL the dashboard is connected to.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Optional GitHub username; when set the PAT is sent as Basic auth.</summary>
    public string? Username { get; set; }

    /// <summary>GitHub personal access token (or OAuth device-flow token) for the API.</summary>
    public string? Pat { get; set; }

    /// <summary>Optional OAuth App client id enabling "Sign in with GitHub" (device flow).</summary>
    public string? GithubClientId { get; set; }

    public string ComposeFile { get; set; } = DefaultComposeFile;

    public string RunnerDirectory { get; set; } = DefaultRunnerDirectory;
}
