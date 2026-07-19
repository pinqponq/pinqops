using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class CaddyfileGeneratorTests
{
    [Fact]
    public void Generate_EmailAndRoutes_GoldenOutput()
    {
        var routes = new CaddyRoutes
        {
            Email = "ops@example.com",
            Routes =
            {
                new CaddyRoute("app.example.com", "pinqops-app-1", 8080),
                new CaddyRoute("grafana.example.com", "pinqops-grafana", 3000),
            },
        };

        var expected =
            "# Managed by pinqops — do not edit; changes are overwritten on apply.\n"
            + "{\n\temail ops@example.com\n}\n"
            + "\napp.example.com {\n\treverse_proxy pinqops-app-1:8080\n}\n"
            + "\ngrafana.example.com {\n\treverse_proxy pinqops-grafana:3000\n}\n";
        Assert.Equal(expected, CaddyfileGenerator.Generate(routes).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Generate_NoEmail_OmitsGlobalBlock()
    {
        var output = CaddyfileGenerator.Generate(new CaddyRoutes());

        Assert.DoesNotContain("email", output);
        Assert.Contains("Managed by pinqops", output);
    }

    [Theory]
    [InlineData("no-dot")]
    [InlineData("bad domain.com")]
    [InlineData("evil.com {")]
    [InlineData("")]
    public void Validate_RejectsBadDomains(string domain)
    {
        Assert.Throws<ArgumentException>(
            () => CaddyRoutesStore.Validate(new CaddyRoute(domain, "container", 80)));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("inject{")]
    [InlineData("")]
    public void Validate_RejectsBadTargets(string target)
    {
        Assert.Throws<ArgumentException>(
            () => CaddyRoutesStore.Validate(new CaddyRoute("app.example.com", target, 80)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Validate_RejectsBadPorts(int port)
    {
        Assert.Throws<ArgumentException>(
            () => CaddyRoutesStore.Validate(new CaddyRoute("app.example.com", "container", port)));
    }

    [Fact]
    public void Validate_RejectsInjectionInEmail()
    {
        Assert.Throws<ArgumentException>(() => CaddyRoutesStore.ValidateEmail("a@b.c\n}\nadmin"));
    }
}

public class CaddyServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly CaddyRoutesStore _store;

    public CaddyServiceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-caddy-tests").FullName;
        _store = new CaddyRoutesStore(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private CaddyService Service(FakeProcessRunner runner) =>
        new(runner, new DockerService(runner), _store);

    [Fact]
    public async Task InstallAsync_EnsuresNetwork_WritesCaddyfile_RunsContainer()
    {
        var runner = new FakeProcessRunner();
        _store.Update(routes => routes.Routes.Add(new CaddyRoute("app.example.com", "pinqops-app", 8080)));

        await Service(runner).InstallAsync();

        Assert.Contains(runner.Invocations, invocation =>
            invocation.Arguments.Contains("network") && invocation.Arguments.Contains("inspect"));
        var run = runner.Invocations.Single(invocation => invocation.Arguments.Contains("run"));
        Assert.Contains("pinqops-caddy", run.Arguments);
        Assert.Contains("80:80", run.Arguments);
        Assert.Contains("443:443", run.Arguments);
        Assert.Contains("443:443/udp", run.Arguments);
        Assert.Contains("pinqops-caddy-data:/data", run.Arguments);
        Assert.Contains(CaddyService.Image, run.Arguments);
        Assert.Contains("reverse_proxy pinqops-app:8080", File.ReadAllText(_store.CaddyfilePath));
    }

    [Fact]
    public async Task InstallAsync_PortConflict_SurfacesDockerError()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("run")
                ? new ProcessResult(125, "", "Bind for 0.0.0.0:80 failed: port is already allocated")
                : new ProcessResult(0, "", ""));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(runner).InstallAsync());
        Assert.Contains("port is already allocated", exception.Message);
    }

    [Fact]
    public async Task ApplyAsync_RegeneratesFile_AndReloads()
    {
        var runner = new FakeProcessRunner();
        _store.Update(routes => routes.Routes.Add(new CaddyRoute("app.example.com", "pinqops-app", 8080)));

        await Service(runner).ApplyAsync();

        var exec = runner.Invocations.Single(invocation => invocation.Arguments.Contains("exec"));
        Assert.Equal(
            new[] { "exec", "pinqops-caddy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile" },
            exec.Arguments);
        Assert.Contains("app.example.com", File.ReadAllText(_store.CaddyfilePath));
    }

    [Fact]
    public async Task ApplyAsync_ReloadFails_FallsBackToRestart()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("exec")
                ? new ProcessResult(1, "", "exec failed")
                : new ProcessResult(0, "", ""));

        await Service(runner).ApplyAsync();

        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("restart"));
    }

    [Fact]
    public async Task IsOnSharedNetworkAsync_ChecksNetworkMembership()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("inspect")
                ? new ProcessResult(0, """{"pinqops-apps":{},"bridge":{}}""", "")
                : new ProcessResult(0, "", ""));

        Assert.True(await Service(runner).IsOnSharedNetworkAsync("pinqops-app"));
    }

    [Fact]
    public void RoutesStore_RoundTrips()
    {
        _store.Update(routes =>
        {
            routes.Email = "ops@example.com";
            routes.Routes.Add(new CaddyRoute("app.example.com", "pinqops-app", 8080));
        });

        var loaded = new CaddyRoutesStore(_directory).Load();
        Assert.Equal("ops@example.com", loaded.Email);
        Assert.Equal(new CaddyRoute("app.example.com", "pinqops-app", 8080), Assert.Single(loaded.Routes));
    }
}
