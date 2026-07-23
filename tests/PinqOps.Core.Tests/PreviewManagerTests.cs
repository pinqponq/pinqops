using PinqOps.Proxy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class PreviewManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _prodCompose;
    private readonly string _proxyDirectory;

    public PreviewManagerTests()
    {
        _root = Directory.CreateTempSubdirectory("pinqops-preview-tests").FullName;
        _prodCompose = Path.Combine(_root, "docker-compose.yml");
        File.WriteAllText(_prodCompose, "services: {}\n");
        _proxyDirectory = Path.Combine(_root, "proxy");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PreviewDeployRequest Request(string prodCompose, int pr, string owner = "Acme", string repo = "Shop") =>
        new(prodCompose, owner, repo, pr, $"ghcr.io/{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}", "sha-abc123", DateTimeOffset.UnixEpoch);

    [Fact]
    public void PreviewDirectory_And_ProjectName_AreDerivedFromPr()
    {
        Assert.Equal(Path.Combine(_root, "previews", "pr-7"), PreviewManager.PreviewDirectory(_prodCompose, 7));
        Assert.Equal(Path.Combine(_root, "previews", "pr-7", "docker-compose.yml"), PreviewManager.PreviewComposeFile(_prodCompose, 7));
        Assert.Equal("shop-pr-7", PreviewManager.PreviewProjectName("Shop", 7));
        Assert.Equal("shop-pr-7-app-1", PreviewManager.PreviewContainerName("Shop", 7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PreviewDirectory_RejectsNonPositivePr(int pr)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewManager.PreviewDirectory(_prodCompose, pr));
    }

    [Fact]
    public async Task Deploy_RunsPullThenUpAgainstThePreviewCompose()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 12));

        Assert.True(result.Succeeded);
        var composeFile = PreviewManager.PreviewComposeFile(_prodCompose, 12);
        Assert.True(File.Exists(composeFile));

        var dockerCalls = runner.Invocations.Where(i => i.FileName == "docker").ToList();
        Assert.Equal("docker compose -f " + composeFile + " pull", dockerCalls[0].CommandLine);
        Assert.Equal("docker compose -f " + composeFile + " up -d", dockerCalls[1].CommandLine);

        // Both must run from the preview's own directory so its per-PR .env
        // (pinned image/tag/host port) is loaded instead of prod's defaults.
        var previewDirectory = PreviewManager.PreviewDirectory(_prodCompose, 12);
        Assert.Equal(previewDirectory, dockerCalls[0].WorkingDirectory);
        Assert.Equal(previewDirectory, dockerCalls[1].WorkingDirectory);
    }

    [Fact]
    public async Task Deploy_WritesComposeWithPreviewProjectName()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        await manager.DeployAsync(Request(_prodCompose, 5));

        var yaml = File.ReadAllText(PreviewManager.PreviewComposeFile(_prodCompose, 5));
        Assert.Equal("shop-pr-5", ComposeProjectName.ReadFrom(yaml));
    }

    [Fact]
    public async Task Deploy_CopiesProdEnvExceptImageTagAndHostPort()
    {
        var prodEnv = PinqOpsStatePaths.EnvFile(_prodCompose);
        EnvFileStore.SetValue(prodEnv, "PINQOPS_IMAGE", "ghcr.io/acme/shop");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_TAG", "sha-prod");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_HOST_PORT", "8080");
        EnvFileStore.SetValue(prodEnv, "PINQOPS_CONTAINER_PORT", "3000");
        EnvFileStore.SetValue(prodEnv, "DB_PASSWORD", "hunter2");

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        var result = await manager.DeployAsync(Request(_prodCompose, 9));

        var previewEnv = PinqOpsStatePaths.EnvFile(PreviewManager.PreviewComposeFile(_prodCompose, 9));

        // Secrets and the container port carry over from prod.
        Assert.Equal("hunter2", EnvFileStore.GetValue(previewEnv, "DB_PASSWORD"));
        Assert.Equal("3000", EnvFileStore.GetValue(previewEnv, "PINQOPS_CONTAINER_PORT"));

        // Image/tag are re-pinned to the PR build; host port is the freshly allocated one.
        Assert.Equal("ghcr.io/acme/shop", EnvFileStore.GetValue(previewEnv, "PINQOPS_IMAGE"));
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(previewEnv, "PINQOPS_TAG"));
        Assert.Equal(result.HostPort.ToString(), EnvFileStore.GetValue(previewEnv, "PINQOPS_HOST_PORT"));
        Assert.NotEqual("8080", EnvFileStore.GetValue(previewEnv, "PINQOPS_HOST_PORT"));
    }

    [Fact]
    public async Task Deploy_FailsWhenPreviewCapReached()
    {
        new PreviewConfigStore(_prodCompose).Save(new PreviewConfig { MaxPreviews = 2 });
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 2))).Succeeded);

        var third = await manager.DeployAsync(Request(_prodCompose, 3));
        Assert.False(third.Succeeded);
        Assert.Contains("limit reached", third.Error);
        Assert.False(Directory.Exists(PreviewManager.PreviewDirectory(_prodCompose, 3)));
    }

    [Fact]
    public async Task Deploy_RedeployingExistingPreviewIsAllowedAtCap()
    {
        new PreviewConfigStore(_prodCompose).Save(new PreviewConfig { MaxPreviews = 1 });
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
        // Same PR again — an update, not a new preview, so the cap does not block it.
        Assert.True((await manager.DeployAsync(Request(_prodCompose, 1))).Succeeded);
    }

    [Fact]
    public async Task Deploy_PullFailure_ReportsErrorAndSkipsUp()
    {
        var runner = new FakeProcessRunner((_, args) =>
            args.Contains("pull") ? new ProcessResult(1, string.Empty, "network is unreachable") : new ProcessResult(0, string.Empty, string.Empty));
        var manager = new PreviewManager(runner, _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 4));

        Assert.False(result.Succeeded);
        Assert.Contains("network is unreachable", result.Error);
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("up"));
    }

    [Fact]
    public async Task List_ReturnsDeployedPreviewsNewestFirst()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 3));
        await manager.DeployAsync(Request(_prodCompose, 11));

        var previews = PreviewManager.List(_prodCompose, "Shop");

        Assert.Equal(new[] { 11, 3 }, previews.Select(p => p.PullRequestNumber));
        Assert.Equal("shop-pr-11", previews[0].ProjectName);
        Assert.NotNull(previews[0].HostPort);
    }

    [Fact]
    public async Task Teardown_RunsDownAndRemovesDirectory()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 6));
        var composeFile = PreviewManager.PreviewComposeFile(_prodCompose, 6);
        runner.Invocations.Clear();

        await manager.TeardownAsync(_prodCompose, "Shop", 6);

        Assert.Contains(runner.Invocations, i => i.CommandLine == $"docker compose -f {composeFile} down -v");
        Assert.False(Directory.Exists(PreviewManager.PreviewDirectory(_prodCompose, 6)));
    }

    [Fact]
    public async Task Teardown_IsIdempotentForAMissingPreview()
    {
        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);

        // Never deployed — must not throw and must not shell out to docker compose down.
        Assert.True(await manager.TeardownAsync(_prodCompose, "Shop", 99));
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("down"));
    }

    [Fact]
    public async Task Deploy_RoutesPreviewSubdomain_WhenAppHasADomain()
    {
        // The preview forwards to the same container port the app listens on in prod.
        EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(_prodCompose), "PINQOPS_CONTAINER_PORT", "3000");
        var store = new DomainConfigStore(_proxyDirectory);
        store.Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry
                {
                    Domain = "shop.example.com",
                    Target = "acme-shop",
                    TargetContainer = "shop-app-1",
                    TargetPort = 3000,
                    Enabled = true,
                },
            ],
        });

        var runner = new FakeProcessRunner();
        var manager = new PreviewManager(runner, _proxyDirectory);
        var result = await manager.DeployAsync(Request(_prodCompose, 8));

        Assert.Equal("https://pr-8.shop.example.com", result.Url);
        var saved = store.Load();
        var previewEntry = Assert.Single(saved.Domains, d => d.Domain == "pr-8.shop.example.com");
        Assert.Equal("shop-pr-8-app-1", previewEntry.TargetContainer);
        Assert.Equal(3000, previewEntry.TargetPort);
        Assert.Equal(PreviewManager.PreviewMarker("Shop", 8), previewEntry.Target);
        Assert.Contains(runner.Invocations, i => i.CommandLine == "docker exec pinqops-proxy caddy reload --config /etc/caddy/Caddyfile");
    }

    [Fact]
    public async Task Teardown_RemovesOnlyItsOwnPreviewRoute()
    {
        var store = new DomainConfigStore(_proxyDirectory);
        store.Save(new DomainConfig
        {
            Domains =
            [
                new DomainEntry { Domain = "shop.example.com", Target = "acme-shop", TargetContainer = "shop-app-1", TargetPort = 3000, Enabled = true },
            ],
        });

        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);
        await manager.DeployAsync(Request(_prodCompose, 8));
        await manager.DeployAsync(Request(_prodCompose, 9));

        await manager.TeardownAsync(_prodCompose, "Shop", 8);

        var saved = store.Load();
        Assert.DoesNotContain(saved.Domains, d => d.Domain == "pr-8.shop.example.com");
        Assert.Contains(saved.Domains, d => d.Domain == "pr-9.shop.example.com");
        Assert.Contains(saved.Domains, d => d.Domain == "shop.example.com");
    }

    [Fact]
    public async Task Deploy_WithoutProxyConfig_StillSucceedsWithNoUrl()
    {
        var manager = new PreviewManager(new FakeProcessRunner(), _proxyDirectory);

        var result = await manager.DeployAsync(Request(_prodCompose, 2));

        Assert.True(result.Succeeded);
        Assert.Null(result.Url);
    }
}
