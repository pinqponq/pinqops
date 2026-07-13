using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class DeployerTests
{
    private const string ComposePath = "/opt/pinqops/docker-compose.yml";

    [Fact]
    public async Task DeployAsync_HappyPath_RunsPullUpPrune_InOrder()
    {
        var runner = new FakeProcessRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(DeployOptions.Create(ComposePath));

        Assert.True(result);
        Assert.Equal(3, runner.Invocations.Count);
        Assert.Equal($"docker compose -f {ComposePath} pull", runner.Invocations[0].CommandLine);
        Assert.Equal($"docker compose -f {ComposePath} up -d", runner.Invocations[1].CommandLine);
        Assert.Equal("docker image prune -f", runner.Invocations[2].CommandLine);
    }

    [Fact]
    public async Task DeployAsync_PullFails_SkipsUp_ReturnsFalse()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("pull")
                ? new ProcessResult(1, string.Empty, "boom")
                : new ProcessResult(0, string.Empty, string.Empty));
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(DeployOptions.Create(ComposePath));

        Assert.False(result);
        Assert.Single(runner.Invocations);
        Assert.Contains("pull", runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task DeployAsync_UpFails_SkipsPrune_ReturnsFalse()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("up")
                ? new ProcessResult(1, string.Empty, "boom")
                : new ProcessResult(0, string.Empty, string.Empty));
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(DeployOptions.Create(ComposePath));

        Assert.False(result);
        Assert.Equal(2, runner.Invocations.Count); // pull + up, no prune
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_PruneDisabled_DoesNotPrune()
    {
        var runner = new FakeProcessRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(DeployOptions.Create(ComposePath, pruneImages: false));

        Assert.True(result);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_PruneFailure_DoesNotFailDeploy()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("prune")
                ? new ProcessResult(1, string.Empty, "prune failed")
                : new ProcessResult(0, string.Empty, string.Empty));
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(DeployOptions.Create(ComposePath));

        Assert.True(result);
        Assert.Equal(3, runner.Invocations.Count);
    }
}
