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
        "rollback" => await RunRollbackAsync(rest),
        "history" => RunHistory(rest),
        "install-runner" => await RunInstallRunnerAsync(rest),
        "preview" => await RunPreviewAsync(rest),
        "mcp" => await PinqOps.Cli.McpServer.RunAsync(),
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
    var composeFilePath = ResolveComposePath(deployArgs);
    var pruneImages = !HasFlag(deployArgs, "--no-prune");
    var timeout = ParseTimeout(GetOption(deployArgs, "--timeout-seconds"));
    var tag = GetOption(deployArgs, "--tag");
    var healthTimeout = ParseHealthTimeout(GetOption(deployArgs, "--health-timeout-seconds"));
    var keepImages = ParseKeepImages(GetOption(deployArgs, "--keep-images"));
    var expectedImage = GetOption(deployArgs, "--image");

    var options = DeployOptions.Create(
        composeFilePath,
        pruneImages,
        timeout,
        tag: tag,
        healthCheckTimeout: healthTimeout,
        keepImages: keepImages,
        trigger: tag is null ? DeployRecordValues.TriggerManual : DeployRecordValues.TriggerCi,
        expectedImage: expectedImage);
    using var notifications = new PinqOps.Notifications.NotificationDispatcher(composeFilePath, Console.WriteLine);
    var deployer = CreateDeployer(composeFilePath, notifications);

    var succeeded = await deployer.DeployAsync(options);
    return succeeded ? 0 : 1;
}

async Task<int> RunRollbackAsync(string[] rollbackArgs)
{
    var composeFilePath = ResolveComposePath(rollbackArgs);
    var history = new DeployHistoryStore(composeFilePath);

    var currentTag = EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(composeFilePath), Deployer.TagVariable);
    var targetTag = GetOption(rollbackArgs, "--to") ?? history.LastSuccessfulTagBefore(currentTag);
    if (targetTag is null)
    {
        Console.Error.WriteLine(
            "error: no rollback target. Deploy history has no earlier successful tag; "
            + "pass one explicitly with --to <tag>.");
        return 1;
    }

    if (!ComposeUsesTagVariable(composeFilePath))
    {
        Console.Error.WriteLine(
            $"error: {composeFilePath} does not reference ${{{Deployer.TagVariable}}}. "
            + $"Change the image line to e.g. 'image: ghcr.io/<owner>/<repo>:${{{Deployer.TagVariable}:-latest}}' first.");
        return 1;
    }

    Console.WriteLine($"rolling back to {targetTag}" + (currentTag is null ? string.Empty : $" (currently {currentTag})"));

    var options = DeployOptions.Create(
        composeFilePath,
        tag: targetTag,
        healthCheckTimeout: ParseHealthTimeout(GetOption(rollbackArgs, "--health-timeout-seconds")),
        trigger: DeployRecordValues.TriggerRollback);
    using var notifications = new PinqOps.Notifications.NotificationDispatcher(composeFilePath, Console.WriteLine);
    var deployer = CreateDeployer(composeFilePath, notifications);

    var succeeded = await deployer.DeployAsync(options);
    return succeeded ? 0 : 1;
}

int RunHistory(string[] historyArgs)
{
    var composeFilePath = ResolveComposePath(historyArgs);
    var records = new DeployHistoryStore(composeFilePath).Load();

    if (HasFlag(historyArgs, "--json"))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            records,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }));
        return 0;
    }

    if (records.Count == 0)
    {
        Console.WriteLine("no deploys recorded yet");
        return 0;
    }

    Console.WriteLine($"{"WHEN (UTC)",-17} {"TAG",-45} {"RESULT",-12} {"TRIGGER",-9} HEALTH");
    foreach (var record in records.Take(15))
    {
        Console.WriteLine(
            $"{record.StartedAt.UtcDateTime:yyyy-MM-dd HH:mm}  {record.Tag,-45} {record.Result,-12} {record.Trigger,-9} {record.HealthCheck}");
    }

    return 0;
}

Deployer CreateDeployer(string composeFilePath, PinqOps.Notifications.NotificationDispatcher notifications) =>
    new(new ProcessRunner(), Console.WriteLine, history: new DeployHistoryStore(composeFilePath), observer: notifications);

static string ResolveComposePath(string[] args) =>
    GetOption(args, "--compose-file")
    ?? Environment.GetEnvironmentVariable("APP_COMPOSE_PATH")
    ?? DefaultComposePath;

static bool ComposeUsesTagVariable(string composeFilePath) =>
    File.Exists(composeFilePath)
    && File.ReadAllText(composeFilePath).Contains($"${{{Deployer.TagVariable}", StringComparison.Ordinal);

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

async Task<int> RunPreviewAsync(string[] previewArgs)
{
    if (previewArgs.Length == 0)
    {
        Console.Error.WriteLine("error: preview needs a subcommand: deploy or teardown");
        return 1;
    }

    var subcommand = previewArgs[0];
    var previewRest = previewArgs.Skip(1).ToArray();
    var composeFilePath = ResolveComposePath(previewRest);
    var pr = ParsePr(GetOption(previewRest, "--pr"));
    var (owner, repo) = ResolvePreviewRepo(previewRest);

    var manager = new PreviewManager(new ProcessRunner(), log: Console.WriteLine);

    switch (subcommand)
    {
        case "deploy":
            var image = GetOption(previewRest, "--image")
                ?? throw new ArgumentException("preview deploy needs --image (e.g. ghcr.io/<owner>/<repo>).");
            var tag = GetOption(previewRest, "--tag")
                ?? throw new ArgumentException("preview deploy needs --tag (e.g. sha-<commit>).");
            var request = new PreviewDeployRequest(composeFilePath, owner, repo, pr, image, tag, DateTimeOffset.UtcNow);
            var result = await manager.DeployAsync(request);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"error: {result.Error}");
                return 1;
            }

            return 0;

        case "teardown":
            await manager.TeardownAsync(composeFilePath, repo, pr);
            return 0;

        default:
            Console.Error.WriteLine($"error: unknown preview subcommand '{subcommand}' (expected deploy or teardown)");
            return 1;
    }
}

static int ParsePr(string? raw)
{
    if (!int.TryParse(raw, out var pr) || pr <= 0)
    {
        throw new ArgumentException($"--pr must be a positive pull request number, got '{raw}'.");
    }

    return pr;
}

// Owner/repo come from --owner/--repo when given, otherwise from the image
// reference (ghcr.io/<owner>/<repo>) — the workflow always passes --image.
static (string Owner, string Repo) ResolvePreviewRepo(string[] args)
{
    var owner = GetOption(args, "--owner");
    var repo = GetOption(args, "--repo");
    if (owner is not null && repo is not null)
    {
        return (owner, repo);
    }

    var image = GetOption(args, "--image");
    if (image is not null)
    {
        var path = image.Contains('/') ? image[(image.IndexOf('/') + 1)..] : image;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            return (owner ?? segments[^2], repo ?? segments[^1]);
        }
    }

    throw new ArgumentException("preview needs --owner and --repo, or an --image to derive them from.");
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

          pinqops deploy [--compose-file <path>] [--tag <image-tag>] [--no-prune]
                         [--timeout-seconds <n>] [--health-timeout-seconds <n>]
                         [--keep-images <n>] [--image <registry/path>]
              Pull the new image and restart the fixed compose project. With
              --tag, pins PINQOPS_TAG in the project's .env so the exact image
              version is recorded and can be rolled back later. With --image
              (e.g. ghcr.io/<owner>/<repo>), verifies the compose file targets
              that image before pulling and fails fast with a clear message if
              the server compose file is stale (e.g. after a repo rename). After
              up -d the services are health-checked (default 60s; 0 skips). The
              newest --keep-images sha-* images (default 5) are kept for rollback.
              Defaults: --compose-file from $APP_COMPOSE_PATH or /opt/pinqops/docker-compose.yml

          pinqops rollback [--to <tag>] [--compose-file <path>] [--health-timeout-seconds <n>]
              Redeploy a previously deployed image tag. Defaults to the last
              successful tag before the current one (from deploy history). Uses
              the locally kept image, so no registry credentials are needed
              within the retention window.

          pinqops history [--compose-file <path>] [--json]
              Show recent deploys (what, when, result, health).

          pinqops install-runner --repo-url <url> --token <token>
                                 [--labels <labels>] [--name <name>]
                                 [--version <runner-version>] [--dir <path>] [--user <user>]
              Install and register a GitHub Actions self-hosted runner as a
              systemd service (outbound-only; no inbound port on the server).

          pinqops preview deploy --pr <n> --image <registry/path> --tag <image-tag>
                                 [--compose-file <path>] [--owner <o>] [--repo <r>]
          pinqops preview teardown --pr <n> [--compose-file <path>] [--repo <r>]
              Bring up (or tear down) a per-PR preview environment as its own
              compose project (<repo>-pr-<n>) on a free host port, next to
              production. deploy reuses production's .env — minus the pinned
              image/tag/host-port — pulls the PR image and starts it, routing
              pr-<n>.<domain> to it when the app has a domain. teardown removes
              the project, its volumes and its route. Both are invoked by the PR
              workflow on the runner. Owner/repo default to the --image path.

          pinqops mcp
              Run a Model Context Protocol server (stdio) that exposes the
              dashboard's API as agent tools — works with any MCP client
              (Claude Code/Desktop, Cursor, the OpenAI Agents SDK / Codex).
              Reads PINQOPS_URL and PINQOPS_TOKEN (a 'pot_…' token from the
              dashboard's Settings → API tokens); PINQOPS_INSECURE=1 accepts a
              self-signed cert.

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

static TimeSpan? ParseHealthTimeout(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    if (!int.TryParse(raw, out var seconds) || seconds < 0)
    {
        throw new ArgumentException($"--health-timeout-seconds must be a non-negative integer (0 skips), got '{raw}'.");
    }

    return TimeSpan.FromSeconds(seconds);
}

static int ParseKeepImages(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return 5;
    }

    if (!int.TryParse(raw, out var count) || count < 1)
    {
        throw new ArgumentException($"--keep-images must be a positive integer, got '{raw}'.");
    }

    return count;
}
