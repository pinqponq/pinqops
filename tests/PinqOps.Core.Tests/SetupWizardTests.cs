using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class SetupWizardTests : IDisposable
{
    private const string RepoUrl = "https://github.com/pinqponq/pinqops";

    private readonly string _tempDirectory;

    public SetupWizardTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "pinqops-setup-" + Path.GetRandomFileName());
    }

    private static SetupWizard Build(FakeProcessRunner runner, FakeGitHubApiClient apiClient, FakePrompt prompt)
    {
        var downloader = new FakeFileDownloader(createFile: true);
        var checker = new PrerequisiteChecker(runner);
        var resolver = new RegistrationTokenResolver(new GhCli(runner), apiClient, prompt);
        var installer = new RunnerInstaller(runner, downloader);
        return new SetupWizard(checker, resolver, installer, prompt);
    }

    [Fact]
    public async Task RunAsync_HappyPath_InstallsRunnerWithResolvedToken()
    {
        // All probes/install steps succeed; gh mints "reg-token-from-gh".
        var runner = new FakeProcessRunner((fileName, arguments) =>
            fileName == "gh" && arguments.Contains("api")
                ? new ProcessResult(0, "reg-token-from-gh", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty));
        var wizard = Build(runner, new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(
            repositoryUrl: RepoUrl,
            installDirectory: _tempDirectory,
            composeFilePath: Path.Combine(_tempDirectory, "docker-compose.yml"));

        var result = await wizard.RunAsync(options);

        Assert.True(result);
        Assert.Contains(runner.Invocations, invocation =>
            invocation.CommandLine.Contains("config.sh") && invocation.CommandLine.Contains("--token reg-token-from-gh"));
    }

    [Fact]
    public async Task RunAsync_MissingPrerequisite_StopsBeforeTokenAndInstall()
    {
        var runner = new FakeProcessRunner((fileName, _) =>
            fileName == "docker"
                ? new ProcessResult(1, string.Empty, "no docker")
                : new ProcessResult(0, string.Empty, string.Empty));
        var apiClient = new FakeGitHubApiClient();
        var wizard = Build(runner, apiClient, new FakePrompt());
        var options = SetupOptions.Create(repositoryUrl: RepoUrl, installDirectory: _tempDirectory);

        var result = await wizard.RunAsync(options);

        Assert.False(result);
        Assert.Empty(apiClient.Calls);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandLine.Contains("config.sh"));
    }

    [Fact]
    public async Task RunAsync_NonInteractive_NoRepoUrl_Throws()
    {
        var wizard = Build(new FakeProcessRunner(), new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(
            nonInteractive: true,
            skipPreflight: true,
            installDirectory: _tempDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => wizard.RunAsync(options));
    }

    [Fact]
    public async Task RunAsync_InstallStepFails_ReturnsFalse()
    {
        var runner = new FakeProcessRunner((fileName, arguments) =>
        {
            if (fileName == "gh" && arguments.Contains("api"))
            {
                return new ProcessResult(0, "reg-token", string.Empty);
            }

            return fileName.EndsWith("config.sh")
                ? new ProcessResult(1, string.Empty, "registration failed")
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var wizard = Build(runner, new FakeGitHubApiClient(), new FakePrompt());
        var options = SetupOptions.Create(repositoryUrl: RepoUrl, installDirectory: _tempDirectory);

        var result = await wizard.RunAsync(options);

        Assert.False(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
