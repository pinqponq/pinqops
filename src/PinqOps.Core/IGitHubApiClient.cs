namespace PinqOps;

/// <summary>
/// Mints a short-lived self-hosted-runner registration token from a GitHub
/// personal access token. Abstracted so the token flow can be tested without
/// network access.
/// </summary>
public interface IGitHubApiClient
{
    Task<string> CreateRegistrationTokenAsync(
        GitHubRepository repository,
        string personalAccessToken,
        CancellationToken cancellationToken = default);
}
