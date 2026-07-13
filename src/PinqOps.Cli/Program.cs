using System.Reflection;
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
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"pinqops {version}");
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
