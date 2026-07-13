using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class RunnerInstallerTests : IDisposable
{
    private readonly string _tempDirectory;

    public RunnerInstallerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "pinqops-tests-" + Path.GetRandomFileName());
    }

    [Fact]
    public async Task InstallAsync_FreshServer_DownloadsExtractsConfiguresAndInstallsService()
    {
        var runner = new FakeProcessRunner();
        var downloader = new FakeFileDownloader(createFile: true);
        var installer = new RunnerInstaller(runner, downloader);

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.True(result);

        // Downloaded the correct runner tarball.
        Assert.Single(downloader.Downloads);
        Assert.Equal(options.DownloadUrl, downloader.Downloads[0].Url);

        // Extract → configure → install service → start service, in order.
        var commandLines = runner.Invocations.Select(invocation => invocation.CommandLine).ToArray();
        Assert.Contains(commandLines, line => line.StartsWith("tar xzf"));
        Assert.Contains(commandLines, line => line.Contains("./config.sh") && line.Contains("--token token-123"));
        Assert.Contains("sudo ./svc.sh install deployer", commandLines);
        Assert.Contains("sudo ./svc.sh start", commandLines);
    }

    [Fact]
    public async Task InstallAsync_ConfigFailure_StopsAndReturnsFalse()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName == "./config.sh"
                ? new ProcessResult(1, string.Empty, "registration failed")
                : new ProcessResult(0, string.Empty, string.Empty));
        var downloader = new FakeFileDownloader(createFile: true);
        var installer = new RunnerInstaller(runner, downloader);

        var options = RunnerInstallOptions.Create(
            "https://github.com/pinqponq/pinqops",
            "token-123",
            installDirectory: _tempDirectory);

        var result = await installer.InstallAsync(options, serviceUser: "deployer");

        Assert.False(result);
        // svc.sh must not run after config.sh fails.
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("svc.sh"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
