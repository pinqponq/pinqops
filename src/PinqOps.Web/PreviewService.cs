using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's read/manage view over the per-PR preview environments the
/// runner creates. Listing scans each app's <c>previews/</c> directory and asks
/// Docker whether the container is up; teardown is a manual fallback for
/// previews whose PR closed while the runner was offline (the workflow normally
/// tears them down itself).
/// </summary>
public sealed class PreviewService
{
    private readonly DockerService _docker;
    private readonly IProcessRunner _runner;

    public PreviewService(DockerService docker, IProcessRunner runner)
    {
        _docker = docker;
        _runner = runner;
    }

    /// <summary>Every preview on disk across all connected apps, with its running state and PR link.</summary>
    public async Task<IReadOnlyList<object>> ListAsync(UiConfig config)
    {
        var results = new List<object>();
        foreach (var app in config.Apps)
        {
            GitHubRepository repository;
            try
            {
                repository = GitHubRepositoryParser.Parse(app.RepoUrl);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var preview in PreviewManager.List(app.ComposeFile, repository.Name))
            {
                var container = PreviewManager.PreviewContainerName(repository.Name, preview.PullRequestNumber);
                var (_, running) = await _docker.ContainerStateAsync(container).ConfigureAwait(false);
                results.Add(new
                {
                    appId = app.Id,
                    pr = preview.PullRequestNumber,
                    projectName = preview.ProjectName,
                    container,
                    hostPort = preview.HostPort,
                    running,
                    prUrl = $"{app.RepoUrl.TrimEnd('/')}/pull/{preview.PullRequestNumber}",
                });
            }
        }

        return results;
    }

    /// <summary>Tears a preview down by hand (idempotent) — the offline-runner fallback.</summary>
    public async Task<object> TeardownAsync(AppConnection app, int pr)
    {
        var repository = GitHubRepositoryParser.Parse(app.RepoUrl);
        var manager = new PreviewManager(_runner, ProxyPaths.DefaultDirectory);
        await manager.TeardownAsync(app.ComposeFile, repository.Name, pr).ConfigureAwait(false);
        return new { ok = true };
    }
}
