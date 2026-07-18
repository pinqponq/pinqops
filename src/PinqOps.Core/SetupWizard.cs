namespace PinqOps;

/// <summary>
/// Orchestrates <c>pinqops setup</c>: check prerequisites, resolve a
/// registration token, install and register the self-hosted runner (reusing
/// <see cref="RunnerInstaller"/>), then print the remaining compose steps.
/// Returns false when a step fails; throws only on invalid configuration.
/// </summary>
public sealed class SetupWizard
{
    private readonly PrerequisiteChecker _prerequisiteChecker;
    private readonly RegistrationTokenResolver _registrationTokenResolver;
    private readonly RunnerInstaller _runnerInstaller;
    private readonly IPrompt _prompt;
    private readonly Action<string>? _log;

    public SetupWizard(
        PrerequisiteChecker prerequisiteChecker,
        RegistrationTokenResolver registrationTokenResolver,
        RunnerInstaller runnerInstaller,
        IPrompt prompt,
        Action<string>? log = null)
    {
        _prerequisiteChecker = prerequisiteChecker ?? throw new ArgumentNullException(nameof(prerequisiteChecker));
        _registrationTokenResolver = registrationTokenResolver ?? throw new ArgumentNullException(nameof(registrationTokenResolver));
        _runnerInstaller = runnerInstaller ?? throw new ArgumentNullException(nameof(runnerInstaller));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _log = log;
    }

    public async Task<bool> RunAsync(SetupOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.SkipPreflight && !await CheckPrerequisitesAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var repository = ResolveRepository(options);

        var registrationToken = await _registrationTokenResolver
            .ResolveAsync(repository, options, cancellationToken)
            .ConfigureAwait(false);

        // A leftover runner registered to a different repository must be
        // de-registered before config.sh will accept the new registration;
        // mint a removal token for the OLD repo (best effort — cleanup falls
        // back to deleting local files without one).
        string? removalToken = null;
        var registeredUrl = RunnerRegistration.ReadUrl(options.InstallDirectory);
        if (registeredUrl is not null)
        {
            try
            {
                var oldRepository = GitHubRepositoryParser.Parse(registeredUrl);
                _log?.Invoke($"existing runner is registered to {oldRepository.Owner}/{oldRepository.Name}; minting a removal token");
                removalToken = await _registrationTokenResolver
                    .TryResolveRemovalTokenAsync(oldRepository, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // Unparseable registration URL — cleanup will force-delete.
            }
        }

        var installOptions = options.ToRunnerInstallOptions(repository.ToUrl(), registrationToken)
            with { RemovalToken = removalToken };
        var installed = await _runnerInstaller
            .InstallAsync(installOptions, options.ServiceUser, cancellationToken)
            .ConfigureAwait(false);

        if (!installed)
        {
            PrintInstallFailureHints(options);
            return false;
        }

        PrintNextSteps(repository, options);
        return true;
    }

    private async Task<bool> CheckPrerequisitesAsync(CancellationToken cancellationToken)
    {
        _log?.Invoke("checking prerequisites (docker, docker compose, tar, systemd)");
        var report = await _prerequisiteChecker.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (report.AllPresent)
        {
            return true;
        }

        _log?.Invoke("missing prerequisites — pinqops will not install them for you:");
        foreach (var missing in report.Missing)
        {
            _log?.Invoke($"  - {missing.Name} not found. {missing.InstallHint}");
        }

        _log?.Invoke("install the above (see docs/SETUP.md section 3), then re-run: pinqops setup");
        return false;
    }

    private GitHubRepository ResolveRepository(SetupOptions options)
    {
        var repositoryUrl = options.RepositoryUrl;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            if (options.NonInteractive)
            {
                throw new ArgumentException(
                    "Repository URL is required. Pass --repo-url https://github.com/<owner>/<repo>.");
            }

            repositoryUrl = _prompt.Ask("GitHub repository URL (https://github.com/<owner>/<repo>): ");
        }

        return GitHubRepositoryParser.Parse(repositoryUrl);
    }

    private void PrintInstallFailureHints(SetupOptions options)
    {
        _log?.Invoke("runner installation failed. Common fixes:");
        _log?.Invoke($"  - Missing native deps (e.g. libicu): sudo {options.InstallDirectory}/bin/installdependencies.sh");
        _log?.Invoke("  - A registration token is short-lived (~1h). Re-run pinqops setup to mint a fresh one.");
    }

    private void PrintNextSteps(GitHubRepository repository, SetupOptions options)
    {
        _log?.Invoke("runner registered and started.");
        var imageReference = $"ghcr.io/{repository.Owner}/{repository.Name}:latest";

        if (File.Exists(options.ComposeFilePath))
        {
            _log?.Invoke($"found {options.ComposeFilePath} — confirm its image is {imageReference}");
        }
        else
        {
            _log?.Invoke($"create the application compose project at {options.ComposeFilePath}:");
            _log?.Invoke($"  sudo mkdir -p {Path.GetDirectoryName(options.ComposeFilePath)}");
            _log?.Invoke($"  # copy deploy/app.docker-compose.example.yml there and set image: {imageReference}");
        }

        _log?.Invoke($"ensure your deploy workflow uses runs-on: [self-hosted, {options.Labels}]");
        _log?.Invoke("then merge a PR into master to trigger your first deploy.");
    }
}
