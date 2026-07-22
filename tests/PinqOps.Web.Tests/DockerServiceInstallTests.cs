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

    [Fact]
    public async Task InstallProxyAsync_PublishesTheWebPortsAndMountsTheCaddyfile()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.InstallProxyAsync("pinqops-proxy", "caddy:2-alpine", "/opt/pinqops/proxy/Caddyfile");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Equal("docker", run.FileName);
        Assert.Contains("80:80", run.Arguments);
        Assert.Contains("443:443", run.Arguments);
        Assert.Contains("443:443/udp", run.Arguments);
        Assert.Contains("/opt/pinqops/proxy/Caddyfile:/etc/caddy/Caddyfile:ro", run.Arguments);
        Assert.Contains("pinqops-proxy-data:/data", run.Arguments);
        Assert.Contains("caddy:2-alpine", run.Arguments);
    }

    [Fact]
    public async Task ExecAsync_RunsTheArgvInsideTheContainer()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.ExecAsync("pinqops-proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile");

        var exec = runner.Invocations.Single(invocation => invocation.Arguments.Contains("exec"));
        Assert.Equal(["exec", "--", "pinqops-proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile"], exec.Arguments);
    }

    [Fact]
    public async Task ExecAsync_RejectsAFlagLikeContainerName()
    {
        var docker = new DockerService(new FakeProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => docker.ExecAsync("--rm", "sh"));
    }

    [Fact]
    public async Task BackupVolumeAsync_TarsTheVolumeReadOnlyIntoTheBackupDir()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.BackupVolumeAsync("pinqops-postgres-data", "/opt/pinqops/backups/db", "20260722-030405.tgz");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("pinqops-postgres-data:/src:ro", run.Arguments);
        Assert.Contains("/opt/pinqops/backups/db:/dst", run.Arguments);
        Assert.Contains("/dst/20260722-030405.tgz", run.Arguments);
    }

    [Fact]
    public async Task RestoreVolumeAsync_ClearsThenExtracts()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.RestoreVolumeAsync("vol", "/opt/pinqops/backups/vol", "20260722-030405.tgz");

        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("vol:/dst", run.Arguments);
        Assert.Contains(run.Arguments, a => a.Contains("find /dst -mindepth 1 -delete") && a.Contains("tar xzf"));
    }

    [Fact]
    public async Task CopyFromContainerAsync_UsesDockerCp()
    {
        var runner = new FakeProcessRunner();
        var docker = new DockerService(runner);

        await docker.CopyFromContainerAsync("pinqops-redis", "/data/dump.rdb", "/opt/pinqops/backups/redis/x.rdb");

        var cp = runner.Invocations.Single(invocation => invocation.Arguments.Contains("cp"));
        Assert.Equal(["cp", "pinqops-redis:/data/dump.rdb", "/opt/pinqops/backups/redis/x.rdb"], cp.Arguments);
    }
}
