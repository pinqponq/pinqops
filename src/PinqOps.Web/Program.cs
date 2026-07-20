using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using PinqOps;
using PinqOps.Web;

// pinqops-ui — the optional web dashboard for a pinqops server.
// Binds 7467 by default ("PINQ" on a phone keypad) — an otherwise unassigned port.

var port = GetOption(args, "--port") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_PORT") ?? "7467";
var host = GetOption(args, "--host") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_HOST") ?? "0.0.0.0";
var certPath = GetOption(args, "--cert") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CERT");
var certPassword = GetOption(args, "--cert-password") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CERT_PASSWORD");
var useTls = !string.IsNullOrWhiteSpace(certPath);

// Subcommands (no command = run the dashboard in the foreground).
if (args.Length > 0 && (!args[0].StartsWith('-') || args[0] is "--version" or "-v" or "--help" or "-h"))
{
    switch (args[0])
    {
        case "install-service":
            return await new ServiceInstaller(new ProcessRunner(), Console.WriteLine).InstallAsync(
                port, host, certPath, certPassword,
                GetOption(args, "--user")
                    ?? Environment.GetEnvironmentVariable("SUDO_USER")
                    ?? Environment.UserName);
        case "uninstall-service":
            return await new ServiceInstaller(new ProcessRunner(), Console.WriteLine).UninstallAsync();
        case "version" or "--version" or "-v":
            Console.WriteLine($"pinqops-ui {PinqOpsVersion.Current}");
            return 0;
        case "help" or "--help" or "-h":
            Console.WriteLine(
                """
                pinqops-ui — optional web dashboard for a pinqops server

                Usage:
                  pinqops-ui [--port <n>] [--host <addr>] [--cert <pfx> [--cert-password <pw>]]
                      Run the dashboard in the foreground (default port 7467).

                  pinqops-ui install-service [--port <n>] [--host <addr>] [--cert <pfx>] [--user <user>]
                      Install + start it as a systemd service (survives SSH logout, starts on boot).
                      The first-run setup code lands in:  journalctl -u pinqops-ui

                  pinqops-ui uninstall-service
                  pinqops-ui version | help
                """);
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown command '{args[0]}' — see 'pinqops-ui help'.");
            return 1;
    }
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"{(useTls ? "https" : "http")}://{host}:{port}");
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // No endpoint accepts more than a small JSON body; cap requests hard.
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
    kestrel.AddServerHeader = false;
    if (useTls)
    {
        kestrel.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(certPath!, certPassword));
    }
});

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<UiConfigStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<GitHubDashboardService>();
builder.Services.AddSingleton<GitHubDeviceFlow>();
builder.Services.AddSingleton<LocalRunnerService>();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<AppInstallJobs>();
builder.Services.AddSingleton<DeployService>();
builder.Services.AddSingleton<AppCredentialStore>();

// Blunt per-client request ceiling on top of the login throttle, so a single
// client cannot hammer the API (or the process-spawning docker endpoints).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var runnerInstallGate = new SemaphoreSlim(1, 1);
var runnerInstallProgress = new ProgressBuffer();

// The dashboard page is one embedded file; cache its bytes and pin its inline
// script with a CSP hash so no other script can ever execute on the page.
var indexBytes = LoadIndexBytes();
var contentSecurityPolicy =
    $"default-src 'none'; script-src '{HashInlineScript(indexBytes)}'; style-src 'unsafe-inline'; "
    + "img-src 'self' data: https://avatars.githubusercontent.com; "
    + "connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

// First-run bootstrap secret: creating the dashboard password requires this
// code from the server console, so whoever reaches the port first cannot
// claim an unconfigured dashboard.
var setupCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["X-Robots-Tag"] = "noindex";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (context.Request.IsHttps)
    {
        headers["Strict-Transport-Security"] = "max-age=31536000";
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }

    await next();
});

// Every /api route except the auth handshake requires a valid session token.
string[] anonymousPaths = ["/api/auth/state", "/api/auth/setup", "/api/auth/login"];
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api")
        && !anonymousPaths.Contains(path.Value, StringComparer.OrdinalIgnoreCase))
    {
        var sessions = context.RequestServices.GetRequiredService<SessionStore>();
        if (ReadBearerToken(context) is not { } token || !sessions.Validate(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
            return;
        }
    }

    await next();
});

// ---- Dashboard page (embedded single file) --------------------------------

app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
    return Results.Bytes(indexBytes, "text/html; charset=utf-8");
});

// ---- Auth ------------------------------------------------------------------

app.MapGet("/api/auth/state", (UiConfigStore store) => Results.Json(new
{
    needsSetup = string.IsNullOrEmpty(store.Current.PasswordHash),
}));

app.MapPost("/api/auth/setup", async (HttpContext context, UiConfigStore store, SessionStore sessions, LoginThrottle throttle) =>
{
    var client = ClientKey(context);
    if (throttle.RetryAfter(client) is { } wait)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
    }

    var request = await context.Request.ReadFromJsonAsync<SetupRequest>();
    if (!string.IsNullOrEmpty(store.Current.PasswordHash))
    {
        return Error(409, "A password is already set — log in instead.");
    }

    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request?.SetupCode?.Trim().ToLowerInvariant() ?? ""),
            Encoding.UTF8.GetBytes(setupCode)))
    {
        throttle.RecordFailure(client);
        logger.LogWarning("Dashboard setup attempt with a wrong setup code from {Client}", client);
        await Task.Delay(500);
        return Error(401, "Wrong setup code — it is printed on the server console where pinqops-ui runs.");
    }

    if (request?.Password is not { Length: >= 8 } password)
    {
        return Error(400, "Choose a password of at least 8 characters.");
    }

    throttle.RecordSuccess(client);
    store.Update(config => config.PasswordHash = PasswordHasher.Hash(password));
    logger.LogWarning("Dashboard password created from {Client}", client);
    return Results.Json(new { token = sessions.Create() });
});

app.MapPost("/api/auth/login", async (HttpContext context, UiConfigStore store, SessionStore sessions, LoginThrottle throttle) =>
{
    var client = ClientKey(context);
    if (throttle.RetryAfter(client) is { } wait)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
    }

    var request = await context.Request.ReadFromJsonAsync<PasswordRequest>();
    var hash = store.Current.PasswordHash;
    if (hash is null)
    {
        return Error(409, "No password set yet — create one first.");
    }

    if (request?.Password is not { } password || !PasswordHasher.Verify(password, hash))
    {
        throttle.RecordFailure(client);
        logger.LogWarning("Failed dashboard login from {Client}", client);
        await Task.Delay(500); // keep failures slow even before the lockout kicks in
        return Error(401, "Wrong password.");
    }

    throttle.RecordSuccess(client);
    if (PasswordHasher.NeedsRehash(hash))
    {
        store.Update(config => config.PasswordHash = PasswordHasher.Hash(password));
    }

    return Results.Json(new { token = sessions.Create() });
});

app.MapPost("/api/auth/logout", (HttpContext context, SessionStore sessions) =>
{
    if (ReadBearerToken(context) is { } token)
    {
        sessions.Revoke(token);
    }

    return Results.Json(new { ok = true });
});

app.MapPost("/api/auth/change-password", async (HttpContext context, UiConfigStore store, SessionStore sessions, LoginThrottle throttle) =>
{
    var client = ClientKey(context);
    if (throttle.RetryAfter(client) is { } wait)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Error(429, $"Too many failed attempts — try again in {(int)Math.Ceiling(wait.TotalMinutes)} minute(s).");
    }

    var request = await context.Request.ReadFromJsonAsync<ChangePasswordRequest>();
    var hash = store.Current.PasswordHash;
    if (hash is null || request?.CurrentPassword is not { } current || !PasswordHasher.Verify(current, hash))
    {
        throttle.RecordFailure(client);
        logger.LogWarning("Failed password change (wrong current password) from {Client}", client);
        await Task.Delay(500);
        return Error(401, "Current password is wrong.");
    }

    if (request.NewPassword is not { Length: >= 8 } fresh)
    {
        return Error(400, "New password must be at least 8 characters.");
    }

    throttle.RecordSuccess(client);
    store.Update(config => config.PasswordHash = PasswordHasher.Hash(fresh));
    sessions.RevokeAll(); // every device must sign in again with the new password
    logger.LogWarning("Dashboard password changed from {Client}; all sessions revoked", client);
    return Results.Json(new { ok = true });
});

// ---- Settings / GitHub connection -------------------------------------------

app.MapGet("/api/settings", (UiConfigStore store) =>
{
    var config = store.Current;

    // The canonical owner/repo display name comes from the server-side parser
    // (works for GHES hosts too) so the UI never re-parses the URL itself.
    string? fullName = null;
    if (!string.IsNullOrWhiteSpace(config.RepoUrl))
    {
        try
        {
            var repository = GitHubRepositoryParser.Parse(config.RepoUrl);
            fullName = $"{repository.Owner}/{repository.Name}";
        }
        catch (ArgumentException)
        {
            // A hand-edited invalid URL just means no pretty name.
        }
    }

    return Results.Json(new
    {
        repoUrl = config.RepoUrl,
        fullName,
        username = config.Username,
        patMasked = config.Pat is { Length: > 4 } pat ? $"••••••••{pat[^4..]}" : null,
        composeFile = config.ComposeFile,
        runnerDirectory = config.RunnerDirectory,
        configPath = store.Path_,
        version = PinqOpsVersion.Current,
        githubClientId = config.GithubClientId
            ?? Environment.GetEnvironmentVariable("PINQOPS_GITHUB_CLIENT_ID"),
    });
});

app.MapPost("/api/settings", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<SettingsRequest>();
        if (request is null || string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            throw new ArgumentException("Repository URL is required.");
        }

        var repository = GitHubRepositoryParser.Parse(request.RepoUrl);
        var pat = string.IsNullOrWhiteSpace(request.Pat) ? store.Current.Pat : request.Pat.Trim();
        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new ArgumentException("A token (PAT) is required to connect.");
        }

        // An absent username means "keep the stored one" (it is set via the
        // token popup); validate with whichever applies.
        var username = request.Username ?? store.Current.Username;

        // Validate the connection before saving anything.
        var repo = await gitHub.TestConnectionAsync(request.RepoUrl, username, pat);

        store.Update(config =>
        {
            config.RepoUrl = repository.ToUrl();
            if (request.Username is not null)
            {
                config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            }

            config.Pat = pat;
            if (!string.IsNullOrWhiteSpace(request.ComposeFile))
            {
                config.ComposeFile = request.ComposeFile.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.RunnerDirectory))
            {
                config.RunnerDirectory = request.RunnerDirectory.Trim();
            }

            if (request.GithubClientId is not null)
            {
                config.GithubClientId = string.IsNullOrWhiteSpace(request.GithubClientId)
                    ? null
                    : request.GithubClientId.Trim();
            }
        });

        return new
        {
            ok = true,
            fullName = repo.TryGetProperty("full_name", out var name) ? name.GetString() : repository.ToUrl(),
            isPrivate = repo.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean(),
        };
    }));

app.MapPost("/api/settings/disconnect", (UiConfigStore store) =>
{
    store.Update(config =>
    {
        config.RepoUrl = null;
        config.Username = null;
        config.Pat = null;
    });
    return Results.Json(new { ok = true });
});

// ---- GitHub (repo, runners, workflow runs) ----------------------------------

app.MapGet("/api/github/overview", (GitHubDashboardService gitHub) =>
    Safe(async () => await gitHub.GetOverviewAsync()));

app.MapGet("/api/github/user", (GitHubDashboardService gitHub) =>
    Safe(async () => await gitHub.GetUserAsync()));

app.MapGet("/api/github/repos", (GitHubDashboardService gitHub) =>
    Safe(async () => new { items = await gitHub.GetReposAsync() }));

// Stash a pasted token (before a repository is chosen); validated via /user.
app.MapPost("/api/github/token", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<TokenRequest>();
        if (request?.Pat is not { Length: > 0 } pat)
        {
            throw new ArgumentException("A token is required.");
        }

        var user = await gitHub.GetUserAsync(request.Username, pat.Trim());
        store.Update(config =>
        {
            config.Pat = pat.Trim();
            config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        });
        return new { ok = true, user };
    }));

// "Sign in with GitHub" (OAuth device flow; needs an OAuth App client id).
app.MapPost("/api/github/device/start", (HttpContext context, UiConfigStore store, GitHubDeviceFlow deviceFlow) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<DeviceStartRequest>();
        var clientId = request?.ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = store.Current.GithubClientId
                ?? Environment.GetEnvironmentVariable("PINQOPS_GITHUB_CLIENT_ID");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("No OAuth App client id configured for GitHub sign-in.");
        }

        var started = await deviceFlow.StartAsync(clientId);
        // Remember a working client id so the next sign-in needs no typing.
        store.Update(config => config.GithubClientId = clientId);
        return started;
    }));

app.MapPost("/api/github/device/poll", (HttpContext context, UiConfigStore store, GitHubDeviceFlow deviceFlow, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<DevicePollRequest>();
        if (request?.Handle is not { Length: > 0 } handle)
        {
            throw new ArgumentException("Missing device-flow handle.");
        }

        var (status, token, intervalSeconds) = await deviceFlow.PollAsync(handle);
        if (status != "success" || token is null)
        {
            return new { status, intervalSeconds };
        }

        var user = await gitHub.GetUserAsync(null, token);
        store.Update(config =>
        {
            config.Pat = token;
            config.Username = null;
        });
        return new { status, user };
    }));

// ---- Docker ------------------------------------------------------------------

app.MapGet("/api/docker/containers", (DockerService docker) =>
    Safe(async () => new { items = await docker.ListContainersAsync() }));

app.MapGet("/api/docker/stats", (DockerService docker) =>
    Safe(async () => new { items = await docker.StatsAsync() }));

app.MapGet("/api/docker/images", (DockerService docker) =>
    Safe(async () => new { items = await docker.ListImagesAsync() }));

app.MapGet("/api/docker/volumes", (DockerService docker) =>
    Safe(async () => new { items = await docker.ListVolumesAsync() }));

app.MapGet("/api/docker/networks", (DockerService docker) =>
    Safe(async () => new { items = await docker.ListNetworksAsync() }));

app.MapGet("/api/docker/networks/{name}/inspect", (string name, DockerService docker) =>
    Safe(async () => new { data = await docker.InspectNetworkAsync(name) }));

app.MapPost("/api/docker/networks", (HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<NetworkCreateRequest>();
        var output = await docker.CreateNetworkAsync(request?.Name ?? "", request?.Driver, request?.Internal ?? false);
        return new { ok = true, output };
    }));

app.MapPost("/api/docker/networks/{name}/remove", (string name, DockerService docker) =>
    Safe(async () => new { ok = true, output = await docker.RemoveNetworkAsync(name) }));

app.MapPost("/api/docker/networks/{name}/connect", (string name, HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<NetworkContainerRequest>();
        return new { ok = true, output = await docker.ConnectNetworkAsync(name, request?.Container ?? "") };
    }));

app.MapPost("/api/docker/networks/{name}/disconnect", (string name, HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<NetworkContainerRequest>();
        return new { ok = true, output = await docker.DisconnectNetworkAsync(name, request?.Container ?? "") };
    }));

app.MapGet("/api/docker/df", (DockerService docker) =>
    Safe(async () => new { items = await docker.SystemDiskUsageAsync() }));

app.MapGet("/api/docker/version", (DockerService docker) =>
    Safe(async () => new { version = await docker.VersionAsync() }));

app.MapGet("/api/docker/containers/{id}/logs", (string id, HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var tail = int.TryParse(context.Request.Query["tail"], out var parsed)
            ? Math.Clamp(parsed, 10, 5000)
            : 200;
        return new { logs = await docker.ContainerLogsAsync(id, tail) };
    }));

app.MapGet("/api/docker/containers/{id}/inspect", (string id, DockerService docker) =>
    Safe(async () => new { data = await docker.InspectContainerAsync(id) }));

app.MapPost("/api/docker/containers/{id}/action", (string id, HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<ContainerActionRequest>();
        var output = await docker.ContainerActionAsync(id, request?.Action ?? "");
        return new { ok = true, output };
    }));

app.MapPost("/api/docker/prune", (DockerService docker) =>
    Safe(async () => new { ok = true, output = await docker.PruneImagesAsync() }));

// ---- Setup (Portainer-style: pick a repo, the dashboard readies everything) ----

app.MapGet("/api/setup/status", (UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var config = store.Current;
        if (!gitHub.IsConfigured)
        {
            return new { configured = false };
        }

        var repoTask = gitHub.CheckRepoSetupAsync();
        var runnersTask = gitHub.GetRunnersSummaryAsync();
        var repo = await repoTask;

        // Listing runners needs repo-admin (Administration: read). A token
        // without it must only degrade the runner row, not kill the card.
        var online = 0;
        var total = 0;
        string? runnersError = null;
        try
        {
            (online, total) = await runnersTask;
        }
        catch (GitHubApiException exception)
        {
            runnersError = exception.Message;
        }

        // "Installed" must mean "registered to THIS repo": a leftover runner
        // from an earlier repository would otherwise short-circuit the setup
        // flow into starting the wrong repo's runner service. One .runner read
        // answers installed/mismatch/registered-to alike.
        var runnerRegisteredTo = LocalRunnerService.GetRegisteredUrl(config.RunnerDirectory);
        var runnerInstalled = runnerRegisteredTo is not null
            && LocalRunnerService.MatchesRepo(runnerRegisteredTo, config.RepoUrl);

        return (object)new
        {
            configured = true,
            repo,
            runnersOnline = online,
            runnersTotal = total,
            runnersError,
            runnerInstalled,
            runnerMismatch = !runnerInstalled && runnerRegisteredTo is not null,
            runnerRegisteredTo,
            composeFile = config.ComposeFile,
            composeExists = File.Exists(config.ComposeFile),
        };
    }));

app.MapPost("/api/setup/create-workflow", (GitHubDashboardService gitHub) =>
    Safe(async () => await gitHub.CreateWorkflowFileAsync(SetupTemplates.DeployWorkflowYaml)));

app.MapPost("/api/setup/start-runner", (UiConfigStore store, LocalRunnerService runner) =>
    Safe(async () => await runner.StartServiceAsync(store.Current.RunnerDirectory)));

app.MapPost("/api/setup/create-compose", (UiConfigStore store, DockerService docker) =>
    Safe(async () =>
    {
        var config = store.Current;
        if (string.IsNullOrWhiteSpace(config.RepoUrl))
        {
            throw new InvalidOperationException("Connect a repository first.");
        }

        if (File.Exists(config.ComposeFile))
        {
            throw new InvalidOperationException($"{config.ComposeFile} already exists.");
        }

        // The template references the shared pinqops-apps network as external;
        // it must exist before the first compose up.
        await docker.EnsureSharedNetworkAsync();

        var repository = GitHubRepositoryParser.Parse(config.RepoUrl);
        var directory = Path.GetDirectoryName(config.ComposeFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(config.ComposeFile, SetupTemplates.ComposeYaml(repository.Owner, repository.Name));
        return new { ok = true, composeFile = config.ComposeFile };
    }));

// Full runner install driven from the dashboard: registration token via the
// stored PAT, then download + config.sh + systemd service (same code path as
// `pinqops install-runner`).
app.MapPost("/api/setup/install-runner", async (UiConfigStore store, IProcessRunner processRunner) =>
{
    if (!await runnerInstallGate.WaitAsync(0))
    {
        return Error(409, "A runner install is already in progress.");
    }

    runnerInstallProgress.Start();
    var succeeded = false;
    try
    {
        var config = store.Current;
        if (string.IsNullOrWhiteSpace(config.RepoUrl) || string.IsNullOrWhiteSpace(config.Pat))
        {
            return Error(400, "Connect GitHub first.");
        }

        runnerInstallProgress.Add("requesting a runner registration token…");
        var repository = GitHubRepositoryParser.Parse(config.RepoUrl);
        string registrationToken;
        string? removalToken = null;
        using (var apiClient = new GitHubApiClient())
        {
            try
            {
                registrationToken = await apiClient.CreateRegistrationTokenAsync(repository, config.Pat);
            }
            catch (GitHubApiException exception)
            {
                runnerInstallProgress.Add("error: " + exception.Message);
                return Results.Json(new { succeeded = false, log = runnerInstallProgress.Text() });
            }

            // A leftover runner registered to another repository must be
            // de-registered first; mint a removal token for THAT repo. Best
            // effort — cleanup falls back to deleting local files without it.
            var registeredUrl = LocalRunnerService.GetRegisteredUrl(config.RunnerDirectory);
            if (registeredUrl is not null)
            {
                try
                {
                    var oldRepository = GitHubRepositoryParser.Parse(registeredUrl);
                    runnerInstallProgress.Add($"existing runner is registered to {oldRepository.Owner}/{oldRepository.Name}; requesting a removal token…");
                    removalToken = await apiClient.CreateRemovalTokenAsync(oldRepository, config.Pat);
                }
                catch (Exception exception) when (exception is GitHubApiException or ArgumentException)
                {
                    runnerInstallProgress.Add("could not get a removal token for the old runner: " + exception.Message);
                }
            }
        }

        runnerInstallProgress.Add("token received; installing the runner…");
        var options = RunnerInstallOptions.Create(config.RepoUrl, registrationToken, installDirectory: config.RunnerDirectory)
            with { RemovalToken = removalToken };
        using var downloader = new HttpFileDownloader();
        var installer = new RunnerInstaller(processRunner, downloader, runnerInstallProgress.Add);
        var serviceUser = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName;
        succeeded = await installer.InstallAsync(options, serviceUser);
        logger.LogWarning("Dashboard runner install finished: {Succeeded}", succeeded);
        return Results.Json(new { succeeded, log = runnerInstallProgress.Text() });
    }
    catch (Exception exception)
    {
        // The wizard may only ever see the progress buffer (when the POST is
        // severed by a proxy timeout), so the failure reason must land there.
        runnerInstallProgress.Add("error: " + exception.Message);
        return Error(500, exception.Message);
    }
    finally
    {
        runnerInstallProgress.Finish(succeeded);
        runnerInstallGate.Release();
    }
});

// Polled by the setup wizard while the install POST above is in flight, so the
// user sees download/extract/configure/service lines live.
app.MapGet("/api/setup/install-runner/progress", () => Results.Json(runnerInstallProgress.Snapshot()));

// ---- App catalog (one-click installs) -------------------------------------------

app.MapGet("/api/apps", (DockerService docker, AppInstallJobs jobs) =>
    Safe(async () =>
    {
        var installedById = new Dictionary<string, (string State, string Ports)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var container in await docker.ListContainersAsync())
            {
                var labels = container.TryGetProperty("Labels", out var l) ? l.GetString() ?? "" : "";
                var appLabel = labels.Split(',').FirstOrDefault(x => x.StartsWith(AppCatalog.Label + "=", StringComparison.Ordinal));
                if (appLabel is not null)
                {
                    installedById[appLabel[(AppCatalog.Label.Length + 1)..]] = (
                        container.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "",
                        container.TryGetProperty("Ports", out var p) ? p.GetString() ?? "" : "");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Docker unreachable — the catalog is still browsable.
        }

        var installing = jobs.ActiveAppIds();
        return new
        {
            items = AppCatalog.Apps.Select(a =>
            {
                var installed = installedById.TryGetValue(a.Id, out var info);
                // The Open link must use the port the container actually
                // binds (the user may have overridden the catalog default).
                var actualHostPort = installed ? ParseFirstHostPort(info.Ports) : null;
                return new
                {
                    id = a.Id,
                    name = a.Name,
                    category = a.Category,
                    image = a.Image,
                    note = a.Note,
                    ports = a.Ports.Select(p => new { host = p.Host, container = p.Container }).ToArray(),
                    installed,
                    installing = installing.Contains(a.Id, StringComparer.OrdinalIgnoreCase),
                    state = installed ? info.State : null,
                    hostPort = actualHostPort ?? (a.Ports.Length > 0 ? a.Ports[0].Host : (int?)null),
                };
            }),
        };
    }));

// Installs run in the background (docker pull can take minutes): the endpoint
// returns a job id immediately and the UI polls the job for pulling→starting→
// done, so progress shows without a page refresh.
app.MapPost("/api/apps/install", async (HttpContext context, DockerService docker, AppInstallJobs jobs) =>
{
    AppInstallRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<AppInstallRequest>();
    }
    catch (System.Text.Json.JsonException)
    {
        return Error(400, "Invalid request body.");
    }

    var appSpec = AppCatalog.Find(request?.Id ?? "");
    if (appSpec is null)
    {
        return Error(400, $"Unknown app '{request?.Id}'.");
    }

    var hostPorts = request?.HostPorts
        ?? (request?.HostPort is { } single ? new[] { single } : null);
    if (hostPorts is not null && hostPorts.Any(p => p is not 0 and (< 1 or > 65535)))
    {
        return Error(400, "Host port must be between 1 and 65535.");
    }

    var job = jobs.TryStart(appSpec.Id);
    if (job is null)
    {
        return Error(409, "An install for this app is already in progress.");
    }

    var credentialStore = context.RequestServices.GetRequiredService<AppCredentialStore>();

    _ = Task.Run(async () =>
    {
        try
        {
            // Credential tokens resolve to per-app generated passwords; a
            // reinstall reuses the stored one so existing volumes keep working.
            var (env, credentials) = AppCatalog.ResolveEnv(appSpec, credentialStore.GetOrCreatePassword);
            if (credentials.Count > 0)
            {
                credentialStore.SetEnv(appSpec.Id, credentials);
            }

            await docker.PullImageAsync(appSpec.Image);
            job.Phase = "starting";
            job.Output = await docker.InstallAppAsync(appSpec, hostPorts, env);
            job.Phase = "done";
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Phase = "error";
            logger.LogWarning("App install '{AppId}' failed: {Message}", appSpec.Id, exception.Message);
        }
    });

    return Results.Json(new { jobId = job.Id });
});

app.MapGet("/api/apps/install/{jobId}", (string jobId, AppInstallJobs jobs) =>
{
    var job = jobs.Find(jobId);
    return job is null
        ? Error(404, "Unknown install job.")
        : Results.Json(new { appId = job.AppId, phase = job.Phase, done = job.Done, error = job.Error, output = job.Output });
});

app.MapPost("/api/apps/{id}/uninstall", (string id, DockerService docker) =>
    Safe(async () =>
    {
        _ = AppCatalog.Find(id) ?? throw new ArgumentException($"Unknown app '{id}'.");
        return new { ok = true, output = await docker.UninstallAppAsync(id) };
    }));

// Stored generated credentials of an installed catalog app (behind dashboard
// auth like everything else). Kept retrievable because volumes outlive the
// container and a reinstall must reuse the same password.
app.MapGet("/api/apps/{id}/credentials", (string id, AppCredentialStore credentials) =>
{
    var appSpec = AppCatalog.Find(id);
    if (appSpec is null)
    {
        return Error(404, $"Unknown app '{id}'.");
    }

    var env = credentials.Get(appSpec.Id);
    return Results.Json(new
    {
        appId = appSpec.Id,
        items = env is null
            ? Array.Empty<object>()
            : env.Where(pair => pair.Key != "password")
                .Select(pair => new { key = pair.Key, value = pair.Value })
                .ToArray<object>(),
        note = appSpec.Note,
    });
});

// ---- Compose project ----------------------------------------------------------

app.MapGet("/api/compose", (UiConfigStore store, DockerService docker) =>
    Safe(async () =>
    {
        var composeFile = store.Current.ComposeFile;
        if (!File.Exists(composeFile))
        {
            return new { composeFile, exists = false, items = new List<System.Text.Json.JsonElement>() };
        }

        return new { composeFile, exists = true, items = await docker.ComposeServicesAsync(composeFile) };
    }));

// ---- Deploy history & rollback ------------------------------------------------

app.MapGet("/api/deploy/state", (UiConfigStore store, DeployService deploys) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        return deploys.GetState(store.Current.ComposeFile);
    }));

app.MapGet("/api/deploy/history", (UiConfigStore store, DeployService deploys) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        return new { items = deploys.History(store.Current.ComposeFile) };
    }));

app.MapPost("/api/deploy/rollback", async (HttpContext context, UiConfigStore store, DeployService deploys) =>
{
    RollbackRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<RollbackRequest>();
    }
    catch (System.Text.Json.JsonException)
    {
        return Error(400, "Invalid request body.");
    }

    if (request?.Tag is not { Length: > 0 } tag)
    {
        return Error(400, "A tag is required.");
    }

    try
    {
        var job = deploys.TryStartRollback(store.Current.ComposeFile, tag);
        if (job is null)
        {
            return Error(409, "A rollback is already in progress.");
        }

        logger.LogWarning("Rollback to {Tag} started from the dashboard", tag);
        return Results.Json(new { jobId = job.Id });
    }
    catch (ArgumentException exception)
    {
        return Error(400, exception.Message);
    }
    catch (InvalidOperationException exception)
    {
        return Error(400, exception.Message);
    }
});

app.MapGet("/api/deploy/job/{jobId}", (string jobId, DeployService deploys) =>
{
    var job = deploys.Find(jobId);
    return job is null
        ? Error(404, "Unknown rollback job.")
        : Results.Json(new { tag = job.Tag, phase = job.Phase, done = job.Done, error = job.Error, log = job.Log() });
});

// ---- Compose project env (.env) -------------------------------------------------

app.MapGet("/api/compose/env", (UiConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var envFile = PinqOpsStatePaths.EnvFile(store.Current.ComposeFile);
        return new
        {
            envFile,
            items = EnvFileStore.GetAll(envFile).Select(pair => new
            {
                key = pair.Key,
                // Values are secrets by assumption; the UI only ever sees a mask.
                masked = pair.Value.Length > 4 ? $"••••{pair.Value[^4..]}" : "••••",
                managed = pair.Key == Deployer.TagVariable,
            }),
        };
    }));

app.MapPost("/api/compose/env", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<ComposeEnvRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        var envFile = PinqOpsStatePaths.EnvFile(store.Current.ComposeFile);

        foreach (var key in request.Remove ?? [])
        {
            if (key == Deployer.TagVariable)
            {
                throw new ArgumentException($"{Deployer.TagVariable} is managed by pinqops deploy/rollback.");
            }

            EnvFileStore.RemoveValue(envFile, key);
        }

        foreach (var (key, value) in request.Set ?? new Dictionary<string, string>())
        {
            if (key == Deployer.TagVariable)
            {
                throw new ArgumentException($"{Deployer.TagVariable} is managed by pinqops deploy/rollback.");
            }

            EnvFileStore.SetValue(envFile, key, value);
        }

        logger.LogWarning("Compose .env edited from the dashboard");
        return new { ok = true };
    }));

// Converts a stale, hardcoded image: line to the env-driven form so the image
// follows the repository (fixes a compose left pointing at a pre-rename name).
// Only the image line is touched — ports, volumes, and env are preserved.
app.MapPost("/api/compose/sync-image", (UiConfigStore store) =>
    Safe(async () =>
    {
        var config = store.Current;
        if (string.IsNullOrWhiteSpace(config.RepoUrl))
        {
            throw new InvalidOperationException("Connect a repository first.");
        }

        if (!File.Exists(config.ComposeFile))
        {
            throw new InvalidOperationException($"{config.ComposeFile} does not exist.");
        }

        var repository = GitHubRepositoryParser.Parse(config.RepoUrl);
        var defaultImage = $"ghcr.io/{repository.Owner.ToLowerInvariant()}/{repository.Name.ToLowerInvariant()}";
        var original = await File.ReadAllTextAsync(config.ComposeFile);
        var updated = ComposeImageRewriter.ToEnvDriven(original, defaultImage);
        if (updated == original)
        {
            return new { ok = true, changed = false, image = defaultImage };
        }

        await File.WriteAllTextAsync(config.ComposeFile, updated);
        logger.LogWarning("Compose image line synced to {Image} from the dashboard", defaultImage);
        return new { ok = true, changed = true, image = defaultImage };
    }));

// New env only takes effect when the containers are recreated.
app.MapPost("/api/compose/apply", (UiConfigStore store, IProcessRunner processRunner) =>
    Safe(async () =>
    {
        var composeFile = store.Current.ComposeFile;
        if (!File.Exists(composeFile))
        {
            throw new InvalidOperationException($"{composeFile} does not exist.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await processRunner.RunAsync(
            "docker", DockerComposeCommandBuilder.Up(composeFile), workingDirectory: null, cts.Token);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"compose up failed: {result.StandardError.Trim()}");
        }

        return new { ok = true, output = result.StandardOutput.Trim() };
    }));

// ---- Notifications --------------------------------------------------------------

app.MapGet("/api/notifications", (UiConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var config = new PinqOps.Notifications.NotificationConfigStore(store.Current.ComposeFile).Load();
        return new
        {
            events = new
            {
                deploySucceeded = config.Events.DeploySucceeded,
                deployFailed = config.Events.DeployFailed,
                healthCheckFailed = config.Events.HealthCheckFailed,
                rolledBack = config.Events.RolledBack,
            },
            webhook = new { enabled = config.Webhook.Enabled, url = config.Webhook.Url },
            slack = new { enabled = config.Slack.Enabled, webhookUrl = config.Slack.WebhookUrl },
            telegram = new
            {
                enabled = config.Telegram.Enabled,
                botTokenMasked = config.Telegram.BotToken is { Length: > 4 } token ? $"••••••••{token[^4..]}" : null,
                chatId = config.Telegram.ChatId,
            },
        };
    }));

app.MapPost("/api/notifications", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<NotificationsRequest>()
            ?? throw new ArgumentException("Invalid request body.");

        var configStore = new PinqOps.Notifications.NotificationConfigStore(store.Current.ComposeFile);
        var config = configStore.Load();

        if (request.Events is { } events)
        {
            config.Events.DeploySucceeded = events.DeploySucceeded ?? config.Events.DeploySucceeded;
            config.Events.DeployFailed = events.DeployFailed ?? config.Events.DeployFailed;
            config.Events.HealthCheckFailed = events.HealthCheckFailed ?? config.Events.HealthCheckFailed;
            config.Events.RolledBack = events.RolledBack ?? config.Events.RolledBack;
        }

        if (request.Webhook is { } webhook)
        {
            config.Webhook.Enabled = webhook.Enabled ?? config.Webhook.Enabled;
            if (webhook.Url is not null)
            {
                config.Webhook.Url = webhook.Url.Trim();
            }
        }

        if (request.Slack is { } slack)
        {
            config.Slack.Enabled = slack.Enabled ?? config.Slack.Enabled;
            if (slack.WebhookUrl is not null)
            {
                config.Slack.WebhookUrl = slack.WebhookUrl.Trim();
            }
        }

        if (request.Telegram is { } telegram)
        {
            config.Telegram.Enabled = telegram.Enabled ?? config.Telegram.Enabled;
            // An absent/blank token means "keep the stored one" (it is masked in GET).
            if (!string.IsNullOrWhiteSpace(telegram.BotToken))
            {
                config.Telegram.BotToken = telegram.BotToken.Trim();
            }

            if (telegram.ChatId is not null)
            {
                config.Telegram.ChatId = telegram.ChatId.Trim();
            }
        }

        // URLs are validated eagerly so a typo is a 400 now, not a silent
        // delivery failure later.
        if (config.Webhook.Enabled && config.Webhook.Url.Length > 0)
        {
            PinqOps.Notifications.WebhookNotifier.ValidateHttpUrl(config.Webhook.Url);
        }

        if (config.Slack.Enabled && config.Slack.WebhookUrl.Length > 0)
        {
            PinqOps.Notifications.WebhookNotifier.ValidateHttpUrl(config.Slack.WebhookUrl);
        }

        configStore.Save(config);
        return new { ok = true };
    }));

app.MapPost("/api/notifications/test", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<NotificationTestRequest>();
        if (request?.Channel is not { Length: > 0 } channel)
        {
            throw new ArgumentException("A channel is required.");
        }

        using var dispatcher = new PinqOps.Notifications.NotificationDispatcher(store.Current.ComposeFile);
        var delivered = await dispatcher.SendTestAsync(channel);
        return new { ok = delivered, delivered };
    }));

// ---- Local runner & system ------------------------------------------------------

app.MapGet("/api/runner/local", (UiConfigStore store, LocalRunnerService runner) =>
    Safe(async () => await runner.GetStatusAsync(store.Current.RunnerDirectory)));

app.MapGet("/api/runner/logs", (HttpContext context, LocalRunnerService runner) =>
    Safe(async () =>
    {
        var unit = context.Request.Query["unit"].ToString();
        if (string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException("A runner unit is required.");
        }

        return new { unit, logs = await runner.GetLogsAsync(unit, 100) };
    }));

app.MapGet("/api/system", (SystemInfoService system) => Results.Json(system.GetInfo()));

Console.WriteLine($"pinqops-ui {PinqOpsVersion.Current} listening on {(useTls ? "https" : "http")}://{host}:{port}");
var configStore = app.Services.GetRequiredService<UiConfigStore>();
if (string.IsNullOrEmpty(configStore.Current.PasswordHash))
{
    Console.WriteLine($"first-run setup code: {setupCode}   (required once, to create the dashboard password)");
}

if (!useTls)
{
    Console.WriteLine("note: serving plain HTTP. Bind --host 127.0.0.1 and tunnel in, or pass --cert <pfx> for TLS.");
}

app.Run();
return 0;

// ---- Helpers ---------------------------------------------------------------------

static async Task<IResult> Safe(Func<Task<object?>> action)
{
    try
    {
        return Results.Json(await action());
    }
    catch (ArgumentException exception)
    {
        return Error(400, exception.Message);
    }
    catch (InvalidOperationException exception)
    {
        return Error(400, exception.Message);
    }
    catch (GitHubApiException exception)
    {
        return Error(502, exception.Message);
    }
    catch (Exception exception)
    {
        return Error(500, exception.Message);
    }
}

static IResult Error(int statusCode, string message) =>
    Results.Json(new { error = message }, statusCode: statusCode);

static string ClientKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>First published host port from docker ps's Ports column, e.g.
/// "0.0.0.0:3005-&gt;3000/tcp, :::3005-&gt;3000/tcp" → 3005.</summary>
static int? ParseFirstHostPort(string portsColumn)
{
    var match = System.Text.RegularExpressions.Regex.Match(portsColumn ?? "", @":(\d+)->");
    return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
}

static string? ReadBearerToken(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
}

static byte[] LoadIndexBytes()
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream("index.html")
        ?? throw new InvalidOperationException("Embedded dashboard page is missing.");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

/// <summary>
/// CSP source for the page's single inline script block: the SHA-256 of the
/// exact bytes between <c>&lt;script&gt;</c> and <c>&lt;/script&gt;</c>.
/// </summary>
static string HashInlineScript(byte[] indexBytes)
{
    var html = Encoding.UTF8.GetString(indexBytes);
    var start = html.IndexOf("<script>", StringComparison.Ordinal);
    var end = html.IndexOf("</script>", StringComparison.Ordinal);
    if (start < 0 || end < 0 || end <= start)
    {
        throw new InvalidOperationException("Embedded dashboard page has no inline script to hash.");
    }

    var script = html[(start + "<script>".Length)..end];
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(script));
    return $"sha256-{Convert.ToBase64String(hash)}";
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
