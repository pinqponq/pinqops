using PinqOps;

const string DefaultComposePath = "/opt/pinqops/docker-compose.yml";

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "setup" => await RunSetupAsync(rest),
        "deploy" => await RunDeployAsync(rest),
        "install-runner" => await RunInstallRunnerAsync(rest),
        "version" or "--version" or "-v" => PrintVersion(),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => Unknown(command),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

async Task<int> RunSetupAsync(string[] setupArgs)
{
    var nonInteractive = HasFlag(setupArgs, "--non-interactive") || Console.IsInputRedirected;

    var options = SetupOptions.Create(
        repositoryUrl: GetOption(setupArgs, "--repo-url") ?? Environment.GetEnvironmentVariable("REPO_URL"),
        personalAccessToken: GetOption(setupArgs, "--pat") ?? Environment.GetEnvironmentVariable("GITHUB_PAT"),
        registrationToken: GetOption(setupArgs, "--token") ?? Environment.GetEnvironmentVariable("RUNNER_TOKEN"),
        composeFilePath: GetOption(setupArgs, "--compose-file") ?? Environment.GetEnvironmentVariable("APP_COMPOSE_PATH"),
        labels: GetOption(setupArgs, "--labels"),
        runnerName: GetOption(setupArgs, "--name"),
        runnerVersion: GetOption(setupArgs, "--version"),
        installDirectory: GetOption(setupArgs, "--dir"),
        serviceUser: GetOption(setupArgs, "--user"),
        nonInteractive: nonInteractive,
        skipPreflight: HasFlag(setupArgs, "--skip-preflight"),
        useGhCli: !HasFlag(setupArgs, "--no-gh"));

    var processRunner = new ProcessRunner();
    using var downloader = new HttpFileDownloader();
    using var gitHubApiClient = new GitHubApiClient();
    var prompt = new ConsolePrompt();

    var prerequisiteChecker = new PrerequisiteChecker(processRunner);
    var ghCli = new GhCli(processRunner, Console.WriteLine);
    var tokenResolver = new RegistrationTokenResolver(ghCli, gitHubApiClient, prompt, Console.WriteLine);
    var installer = new RunnerInstaller(processRunner, downloader, Console.WriteLine);
    var wizard = new SetupWizard(prerequisiteChecker, tokenResolver, installer, prompt, Console.WriteLine);

    var succeeded = await wizard.RunAsync(options);
    return succeeded ? 0 : 1;
}

async Task<int> RunDeployAsync(string[] deployArgs)
{
    var composeFilePath = GetOption(deployArgs, "--compose-file")
        ?? Environment.GetEnvironmentVariable("APP_COMPOSE_PATH")
        ?? DefaultComposePath;
    var pruneImages = !HasFlag(deployArgs, "--no-prune");
    var timeout = ParseTimeout(GetOption(deployArgs, "--timeout-seconds"));

    var options = DeployOptions.Create(composeFilePath, pruneImages, timeout);
    var deployer = new Deployer(new ProcessRunner(), Console.WriteLine);

    var succeeded = await deployer.DeployAsync(options);
    return succeeded ? 0 : 1;
}

async Task<int> RunInstallRunnerAsync(string[] installArgs)
{
    var options = RunnerInstallOptions.Create(
        repositoryUrl: GetOption(installArgs, "--repo-url") ?? Environment.GetEnvironmentVariable("REPO_URL"),
        registrationToken: GetOption(installArgs, "--token") ?? Environment.GetEnvironmentVariable("RUNNER_TOKEN"),
        labels: GetOption(installArgs, "--labels"),
        runnerName: GetOption(installArgs, "--name"),
        runnerVersion: GetOption(installArgs, "--version"),
        installDirectory: GetOption(installArgs, "--dir"));

    var serviceUser = GetOption(installArgs, "--user") ?? Environment.UserName;

    using var downloader = new HttpFileDownloader();
    var installer = new RunnerInstaller(new ProcessRunner(), downloader, Console.WriteLine);

    var succeeded = await installer.InstallAsync(options, serviceUser);
    return succeeded ? 0 : 1;
}

int PrintVersion()
{
    Console.WriteLine($"pinqops {PinqOpsVersion.Current}");
    return 0;
}

int Unknown(string unknownCommand)
{
    Console.Error.WriteLine($"error: unknown command '{unknownCommand}'");
    PrintUsage();
    return 1;
}

int PrintUsage()
{
    Console.WriteLine(
        """
        pinqops — minimal DevOps CLI for closed-server Docker deploys

        Usage:
          pinqops setup [--repo-url <url>] [--pat <pat>] [--token <registration-token>]
                        [--compose-file <path>] [--labels <l>] [--name <name>]
                        [--version <runner-version>] [--dir <path>] [--user <user>]
                        [--no-gh] [--skip-preflight] [--non-interactive]
              Guided onboarding for a fresh server: check prerequisites, obtain a
              runner registration token (authenticated gh CLI, a PAT via the
              GitHub API, or a pasted token), install the self-hosted runner, and
              print the remaining compose steps. Run it and answer the prompts.

          pinqops deploy [--compose-file <path>] [--no-prune] [--timeout-seconds <n>]
              Pull the new image and restart the fixed compose project.
              Defaults: --compose-file from $APP_COMPOSE_PATH or /opt/pinqops/docker-compose.yml

          pinqops install-runner --repo-url <url> --token <token>
                                 [--labels <labels>] [--name <name>]
                                 [--version <runner-version>] [--dir <path>] [--user <user>]
              Install and register a GitHub Actions self-hosted runner as a
              systemd service (outbound-only; no inbound port on the server).

          pinqops version
          pinqops help
        """);
    return 0;
}

static string? GetOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index] == name)
        {
            return args[index + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

static TimeSpan? ParseTimeout(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    if (!int.TryParse(raw, out var seconds) || seconds <= 0)
    {
        throw new ArgumentException($"--timeout-seconds must be a positive integer, got '{raw}'.");
    }

    return TimeSpan.FromSeconds(seconds);
}
