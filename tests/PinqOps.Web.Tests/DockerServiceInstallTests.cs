using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerServiceInstallTests
{
    [Fact]
    public async Task InstallAppAsync_UsesResolvedEnv_NotTheRawSpec()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);
        var spec = AppCatalog.Find("postgres")!;
        var (env, _) = AppCatalog.ResolveEnv(spec, _ => "s3cret");

        await docker.InstallAppAsync(spec, hostPorts: null, env);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("POSTGRES_PASSWORD=s3cret", run.Arguments);
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains("{{password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAppAsync_WithoutOverride_UsesSpecEnv()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);
        var spec = AppCatalog.Find("elasticsearch")!;

        await docker.InstallAppAsync(spec, hostPorts: null);

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("discovery.type=single-node", run.Arguments);
    }
}
