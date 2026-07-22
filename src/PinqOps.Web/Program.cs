using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using PinqOps;
using PinqOps.Backups;
using PinqOps.Proxy;
using PinqOps.Web;
using static System.Globalization.CultureInfo;

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
builder.Services.AddSingleton<ProxyService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton(sp => new PinqOps.Backups.BackupConfigStore(
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "backups.json")));
builder.Services.AddSingleton(sp => new ApiTokenStore(
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "tokens.json")));
builder.Services.AddHostedService<BackupScheduler>();
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
        var token = ReadBearerToken(context);
        if (token is { } bearer && ApiTokenStore.LooksLikeToken(bearer))
        {
            // API-token auth: validate, then enforce the route's required scope.
            // (Session logins are full admins and skip the scope check below.)
            var tokens = context.RequestServices.GetRequiredService<ApiTokenStore>();
            var scope = tokens.Validate(bearer, DateTimeOffset.UtcNow);
            if (scope is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
                return;
            }

            var required = ApiScopes.RequiredFor(context.Request.Method, path.Value ?? string.Empty);
            if (!ApiScopes.Satisfies(scope, required))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = $"This token has '{scope}' scope; '{required}' is required for this action.",
                });
                return;
            }

            context.Items["scope"] = scope;
        }
        else
        {
            var sessions = context.RequestServices.GetRequiredService<SessionStore>();
            if (token is null || !sessions.Validate(token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
                return;
            }
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
    static string? FullName(string repoUrl)
    {
        try
        {
            var repository = GitHubRepositoryParser.Parse(repoUrl);
            return $"{repository.Owner}/{repository.Name}";
        }
        catch (ArgumentException)
        {
            // A hand-edited invalid URL just means no pretty name.
            return null;
        }
    }

    return Results.Json(new
    {
        username = config.Username,
        patMasked = config.Pat is { Length: > 4 } pat ? $"••••••••{pat[^4..]}" : null,
        configPath = store.Path_,
        version = PinqOpsVersion.Current,
        githubClientId = config.GithubClientId
            ?? Environment.GetEnvironmentVariable("PINQOPS_GITHUB_CLIENT_ID"),
        apps = config.Apps.Select(a => new
        {
            id = a.Id,
            repoUrl = a.RepoUrl,
            fullName = FullName(a.RepoUrl),
            composeFile = a.ComposeFile,
            runnerDirectory = a.RunnerDirectory,
        }),
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

        AppConnection? connection = null;
        store.Update(config =>
        {
            if (request.Username is not null)
            {
                config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            }

            config.Pat = pat;
            if (request.GithubClientId is not null)
            {
                config.GithubClientId = string.IsNullOrWhiteSpace(request.GithubClientId)
                    ? null
                    : request.GithubClientId.Trim();
            }

            // One repo = one app: same URL returns the existing connection,
            // an explicit AppId edits that app, anything else creates one.
            connection = AppUpsert.Apply(
                config, request.AppId, repository, request.ComposeFile, request.RunnerDirectory);
        });

        logger.LogWarning("App '{AppId}' connected to {Repo}", connection!.Id, connection.RepoUrl);
        return new
        {
            ok = true,
            appId = connection.Id,
            fullName = repo.TryGetProperty("full_name", out var name) ? name.GetString() : repository.ToUrl(),
            isPrivate = repo.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean(),
        };
    }));

// Signing out of GitHub drops the token but keeps the app connections — they
// are unusable until re-auth, and nothing on disk is touched.
app.MapPost("/api/settings/disconnect", (UiConfigStore store) =>
{
    store.Update(config =>
    {
        config.Username = null;
        config.Pat = null;
    });
    return Results.Json(new { ok = true });
});

// Removes an app from the dashboard ONLY. The compose project (with .pinqops
// state and volumes) and the runner stay on disk — deleting live infrastructure
// has too many failure modes; re-adding the repo re-attaches to the same paths.
app.MapPost("/api/settings/apps/remove", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<AppRemoveRequest>();
        if (request?.Id is not { Length: > 0 } id)
        {
            throw new ArgumentException("An app id is required.");
        }

        AppConnection? removed = null;
        store.Update(config =>
        {
            removed = config.Apps.FirstOrDefault(a => string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown app '{id.Trim()}'.");
            config.Apps.Remove(removed);
        });

        logger.LogWarning("App '{AppId}' removed from the dashboard (files kept)", removed!.Id);
        return new
        {
            ok = true,
            kept = new[] { Path.GetDirectoryName(removed.ComposeFile), removed.RunnerDirectory },
        };
    }));

// ---- GitHub (repo, runners, workflow runs) ----------------------------------

app.MapGet("/api/github/overview", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () => await gitHub.GetOverviewAsync(ResolveApp(store, context))));

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

app.MapGet("/api/setup/status", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub, LocalRunnerService runner) =>
    Safe(async () =>
    {
        if (store.Current.Apps.Count == 0 || !gitHub.HasToken)
        {
            return new { configured = false };
        }

        var app = ResolveApp(store, context);
        if (!gitHub.IsConfiguredFor(app))
        {
            return new { configured = false };
        }

        var repoTask = gitHub.CheckRepoSetupAsync(app);
        var runnersTask = gitHub.GetRunnersSummaryAsync(app);
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
        var runnerRegisteredTo = LocalRunnerService.GetRegisteredUrl(app.RunnerDirectory);
        var runnerInstalled = runnerRegisteredTo is not null
            && LocalRunnerService.MatchesRepo(runnerRegisteredTo, app.RepoUrl);

        // Whether the systemd service is up lets the dashboard auto-start a
        // stopped runner (safe, idempotent) and avoid offering a useless "start"
        // button when the service is running but just can't reach GitHub.
        var runnerServiceActive = runnerInstalled
            ? await runner.IsServiceActiveAsync(app.RunnerDirectory)
            : null;

        return (object)new
        {
            configured = true,
            appId = app.Id,
            repo,
            runnersOnline = online,
            runnersTotal = total,
            runnersError,
            runnerInstalled,
            runnerServiceActive,
            runnerMismatch = !runnerInstalled && runnerRegisteredTo is not null,
            runnerRegisteredTo,
            composeFile = app.ComposeFile,
            composeExists = File.Exists(app.ComposeFile),
        };
    }));

// The workflow is committed to the repository's default branch, so that is the
// branch it must trigger on — a hardcoded one would simply never fire.
app.MapPost("/api/setup/create-workflow", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        var defaultBranch = await gitHub.GetDefaultBranchAsync(app);
        var result = await gitHub.CreateWorkflowFileAsync(app, SetupTemplates.DeployWorkflowYaml(defaultBranch));
        logger.LogWarning("Deploy workflow committed, triggering on {Branch}", defaultBranch);
        return result;
    }));

// Pins the repository variable the generated workflow reads its compose path
// from. A standalone, idempotent step (create-compose is skipped once the file
// exists, so it could never repair a missing/stale variable on a republish).
app.MapPost("/api/setup/app-var", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        await gitHub.SetRepositoryVariableAsync(app, "APP_COMPOSE_PATH", app.ComposeFile);
        logger.LogWarning("APP_COMPOSE_PATH set to {Path} for {Repo}", app.ComposeFile, app.RepoUrl);
        return new { ok = true, name = "APP_COMPOSE_PATH", value = app.ComposeFile };
    }));

// Detects the stack of a repo that has no Dockerfile and returns a generated,
// editable Dockerfile per candidate — pinqops' answer to "zero config".
app.MapGet("/api/setup/detect-stack", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        var branch = await gitHub.GetDefaultBranchAsync(app);
        var (paths, truncated) = await gitHub.GetRepoTreeAsync(app, branch);

        // First pass (no contents) finds the candidate directories and kinds;
        // then fetch only those dirs' manifests to enrich the build hints.
        var firstPass = StackDetector.Detect(paths, _ => null);
        var contents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var candidate in firstPass)
        {
            var prefix = candidate.ManifestDir.Length == 0 ? "" : candidate.ManifestDir + "/";
            foreach (var manifest in paths.Where(p => IsDirectManifest(p, prefix)))
            {
                contents.TryAdd(manifest, null);
            }
        }

        foreach (var key in contents.Keys.ToList())
        {
            contents[key] = await gitHub.GetFileContentAsync(app, key);
        }

        var results = StackDetector.Detect(paths, p => contents.GetValueOrDefault(p));
        return new
        {
            truncated,
            candidates = results.Select(r => new
            {
                kind = r.Kind.ToString().ToLowerInvariant(),
                suggestedPort = r.SuggestedPort,
                dir = r.ManifestDir,
                hints = r.BuildHints,
                dockerfile = DockerfileTemplates.For(r),
            }),
        };
    }));

// Commits the (user-edited) Dockerfile verbatim. For a monorepo subdirectory it
// also pins PINQOPS_BUILD_CONTEXT so the workflow builds from there.
app.MapPost("/api/setup/create-dockerfile", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        var request = await context.Request.ReadFromJsonAsync<CreateDockerfileRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Dockerfile content is required.");
        }

        if (request.Content.Length > 48 * 1024)
        {
            throw new ArgumentException("Dockerfile is too large.");
        }

        var dir = (request.Dir ?? string.Empty).Trim().Trim('/');
        var path = dir.Length == 0 ? "Dockerfile" : $"{dir}/Dockerfile";
        object result;
        try
        {
            result = await gitHub.CreateFileAsync(
                app, path, "chore: add Dockerfile (generated by pinqops-ui)", request.Content);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == 422)
        {
            throw new InvalidOperationException($"{path} already exists in the repository.");
        }

        if (dir.Length > 0)
        {
            await gitHub.SetRepositoryVariableAsync(app, "PINQOPS_BUILD_CONTEXT", dir);
            logger.LogWarning("PINQOPS_BUILD_CONTEXT set to {Dir} for {Repo}", dir, app.RepoUrl);
        }

        logger.LogWarning("Dockerfile committed to {Path} for {Repo}", path, app.RepoUrl);
        return result;
    }));

app.MapPost("/api/setup/start-runner", (HttpContext context, UiConfigStore store, LocalRunnerService runner) =>
    Safe(async () => await runner.StartServiceAsync(ResolveApp(store, context).RunnerDirectory)));

app.MapPost("/api/setup/create-compose", (HttpContext context, UiConfigStore store, DockerService docker, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        // The wizard sends its port choices; older callers send no body at all.
        ComposeCreateRequest? request = null;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ComposeCreateRequest>();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var appConnection = ResolveApp(store, context);
        var repository = GitHubRepositoryParser.Parse(appConnection.RepoUrl);
        var project = ComposeProjectName.FromRepository(repository.Name);

        if (File.Exists(appConnection.ComposeFile))
        {
            // One compose project per path. Silently sharing it between two
            // repositories is the worst outcome: the second repository's deploy
            // pins ITS tag onto the FIRST one's image and dies pulling a tag that
            // only exists in the other package.
            var owner = ComposeProjectName.ReadFrom(await File.ReadAllTextAsync(appConnection.ComposeFile));
            if (owner is not null && owner != project)
            {
                throw new InvalidOperationException(
                    $"{appConnection.ComposeFile} already belongs to '{owner}', not '{project}'. pinqops manages "
                    + $"one application per compose file. Give this app its own path (Advanced → compose file, "
                    + $"e.g. /opt/pinqops/apps/{project}/docker-compose.yml) — the publish step keeps the "
                    + $"APP_COMPOSE_PATH repository variable in sync automatically.");
            }

            throw new InvalidOperationException($"{appConnection.ComposeFile} already exists.");
        }

        // The template references the shared pinqops-apps network as external;
        // it must exist before the first compose up.
        await docker.EnsureSharedNetworkAsync();

        var directory = Path.GetDirectoryName(appConnection.ComposeFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Publishing a port is what makes the deployed app actually reachable.
        // The container side comes from the repo's own Dockerfile so the mapping
        // is right without asking; the host side is a safe default the user can
        // change later from the .env editor. Reading the Dockerfile is only a
        // hint — a GitHub hiccup must not block creating the project.
        int? exposedPort = null;
        try
        {
            exposedPort = await gitHub.GetDockerfileExposedPortAsync(appConnection);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Could not read the Dockerfile's EXPOSE; defaulting the container port");
        }

        var containerPort = SetupPorts.ResolveContainer(request?.ContainerPort, exposedPort);

        // Nothing owns the app's port yet, so this is the one moment a bind test
        // is meaningful. Taking the next free port beats generating a project
        // whose first deploy dies on "port is already allocated".
        var hostPort = SetupPorts.ResolveHost(
            request?.HostPort, UiConfig.DefaultHostPort, HostPort.IsAvailable, HostPort.FindAvailable);
        if (request?.HostPort is null && hostPort != UiConfig.DefaultHostPort)
        {
            logger.LogWarning(
                "Host port {Default} is in use; publishing on {Port} instead", UiConfig.DefaultHostPort, hostPort);
        }

        await File.WriteAllTextAsync(
            appConnection.ComposeFile,
            SetupTemplates.ComposeYaml(repository.Owner, repository.Name, hostPort, containerPort));

        // Seed the .env so both ports are discoverable (and editable) in the
        // dashboard instead of being invisible defaults inside the YAML.
        var envFile = PinqOpsStatePaths.EnvFile(appConnection.ComposeFile);
        EnvFileStore.SetValue(envFile, SetupTemplates.HostPortVariable, hostPort.ToString(InvariantCulture));
        EnvFileStore.SetValue(envFile, SetupTemplates.ContainerPortVariable, containerPort.ToString(InvariantCulture));

        logger.LogWarning(
            "Compose project created at {File} publishing {HostPort}->{ContainerPort}",
            appConnection.ComposeFile, hostPort, containerPort);
        return new { ok = true, composeFile = appConnection.ComposeFile, hostPort, containerPort };
    }));

// Pre-publish data for the wizard's port form: the detected container port and
// a suggested free host port, plus the current .env values once the compose
// project exists. The generic .env endpoint masks every value, so the wizard
// needs this dedicated, ports-only view.
app.MapGet("/api/setup/publish-info", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);

        // The Dockerfile read is a hint — a GitHub hiccup must degrade to
        // "nothing detected", not break the form.
        int? detectedContainerPort = null;
        try
        {
            detectedContainerPort = await gitHub.GetDockerfileExposedPortAsync(app);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the Dockerfile's EXPOSE for the publish form");
        }

        var composeExists = File.Exists(app.ComposeFile);
        var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);
        var currentHostPort = composeExists
            ? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable))
            : null;
        var currentContainerPort = composeExists
            ? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.ContainerPortVariable))
            : null;

        return new
        {
            composeExists,
            detectedContainerPort,
            fallbackContainerPort = DockerfileInspector.DefaultPort,
            suggestedHostPort = currentHostPort
                ?? HostPort.FindAvailable(UiConfig.DefaultHostPort)
                ?? UiConfig.DefaultHostPort,
            currentHostPort,
            currentContainerPort,
        };
    }));

// Live validation while the user types a host port in the wizard.
app.MapGet("/api/setup/port-check", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var raw = context.Request.Query["port"].ToString().Trim();
        if (!int.TryParse(raw, NumberStyles.None, InvariantCulture, out var port) || !HostPort.IsValid(port))
        {
            return (object)new { port = raw, valid = false, available = false };
        }

        // The app's own container legitimately owns its current host port — a
        // bind probe would flag it as busy, so treat "unchanged" as free.
        var appConnection = ResolveApp(store, context);
        var envFile = PinqOpsStatePaths.EnvFile(appConnection.ComposeFile);
        var currentHostPort = File.Exists(appConnection.ComposeFile)
            ? EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable)
            : null;
        var available = currentHostPort == port.ToString(InvariantCulture) || HostPort.IsAvailable(port);
        return (object)new { port, valid = true, available };
    }));

// Live state of the deployed app for the wizard's "your app is live" card:
// whether the compose container runs and on which host port it is reachable.
app.MapGet("/api/setup/app-status", (HttpContext context, UiConfigStore store, DockerService docker) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        var composeExists = File.Exists(app.ComposeFile);
        var envFile = PinqOpsStatePaths.EnvFile(app.ComposeFile);

        string? state = null;
        int? publishedPort = null;
        var dockerOk = true;
        if (composeExists)
        {
            try
            {
                (state, publishedPort) = ComposeAppStatus.FromServices(
                    await docker.ComposeServicesAsync(app.ComposeFile));
            }
            catch (InvalidOperationException)
            {
                dockerOk = false;
            }
        }

        return new
        {
            composeExists,
            dockerOk,
            state,
            running = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
            // Prefer the port docker actually bound; before the first deploy
            // fall back to what the .env says will be published.
            hostPort = publishedPort ?? TryParsePort(EnvFileStore.GetValue(envFile, SetupTemplates.HostPortVariable)),
            currentTag = EnvFileStore.GetValue(envFile, Deployer.TagVariable),
            currentDeployedAt = new DeployHistoryStore(app.ComposeFile).LastSuccessful()?.StartedAt,
        };
    }));

// Starts the first deploy right from the wizard instead of waiting for a push:
// dispatches the generated workflow on the repository's default branch.
app.MapPost("/api/setup/trigger-deploy", (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
    Safe(async () =>
    {
        var app = ResolveApp(store, context);
        var branch = await gitHub.GetDefaultBranchAsync(app);

        // A workflow the wizard committed seconds ago may not be indexed by
        // the Actions API yet — 404s briefly even though the file is there.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await gitHub.TriggerDeployWorkflowAsync(app, branch);
                break;
            }
            catch (GitHubApiException exception) when (exception.StatusCode == 404 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        logger.LogWarning("First deploy triggered via workflow_dispatch on {Branch}", branch);
        return new { ok = true, branch };
    }));

// Full runner install driven from the dashboard: registration token via the
// stored PAT, then download + config.sh + systemd service (same code path as
// `pinqops install-runner`).
app.MapPost("/api/setup/install-runner", async (HttpContext context, UiConfigStore store, IProcessRunner processRunner) =>
{
    // One install at a time across ALL apps: the installer sets a process-wide
    // env var and downloads are huge — serialize, and stamp the buffer with the
    // app so a poller for another app can ignore foreign lines.
    AppConnection appConnection;
    try
    {
        appConnection = ResolveApp(store, context);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return Error(400, exception.Message);
    }

    if (!await runnerInstallGate.WaitAsync(0))
    {
        var busyFor = runnerInstallProgress.AppId;
        return Error(409, busyFor is null
            ? "A runner install is already in progress."
            : $"A runner install is already in progress (app '{busyFor}').");
    }

    runnerInstallProgress.Start(appConnection.Id);
    var succeeded = false;
    try
    {
        var config = store.Current;
        if (string.IsNullOrWhiteSpace(appConnection.RepoUrl) || string.IsNullOrWhiteSpace(config.Pat))
        {
            return Error(400, "Connect GitHub first.");
        }

        runnerInstallProgress.Add("requesting a runner registration token…");
        var repository = GitHubRepositoryParser.Parse(appConnection.RepoUrl);
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
            var registeredUrl = LocalRunnerService.GetRegisteredUrl(appConnection.RunnerDirectory);
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
        var options = RunnerInstallOptions.Create(
                appConnection.RepoUrl, registrationToken,
                // Per-app agent name so `docker ps`-style debugging reads well;
                // names are repo-scoped on GitHub, so this is cosmetic but nice.
                runnerName: $"{Environment.MachineName}-{appConnection.Id}",
                installDirectory: appConnection.RunnerDirectory)
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

app.MapGet("/api/compose", (HttpContext context, UiConfigStore store, DockerService docker) =>
    Safe(async () =>
    {
        var composeFile = ResolveApp(store, context).ComposeFile;
        if (!File.Exists(composeFile))
        {
            return new { composeFile, exists = false, items = new List<System.Text.Json.JsonElement>() };
        }

        return new { composeFile, exists = true, items = await docker.ComposeServicesAsync(composeFile) };
    }));

// ---- Deploy history & rollback ------------------------------------------------

app.MapGet("/api/deploy/state", (HttpContext context, UiConfigStore store, DeployService deploys) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        return deploys.GetState(ResolveApp(store, context).ComposeFile);
    }));

app.MapGet("/api/deploy/history", (HttpContext context, UiConfigStore store, DeployService deploys) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        return new { items = deploys.History(ResolveApp(store, context).ComposeFile) };
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
        var job = deploys.TryStartRollback(ResolveApp(store, context).ComposeFile, tag);
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

app.MapGet("/api/compose/env", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var envFile = PinqOpsStatePaths.EnvFile(ResolveApp(store, context).ComposeFile);
        return new
        {
            envFile,
            items = EnvFileStore.GetAll(envFile).Select(pair => new
            {
                key = pair.Key,
                // Values are secrets by assumption; the UI only ever sees a mask.
                masked = pair.Value.Length > 4 ? $"••••{pair.Value[^4..]}" : "••••",
                managed = Deployer.IsDeployManagedVariable(pair.Key),
            }),
        };
    }));

app.MapPost("/api/compose/env", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<ComposeEnvRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        var envFile = PinqOpsStatePaths.EnvFile(ResolveApp(store, context).ComposeFile);

        // Same predicate the GET projection reports as `managed`, so the editor
        // never offers an edit the write path will reject.
        static void RejectIfDeployManaged(string key)
        {
            if (Deployer.IsDeployManagedVariable(key))
            {
                throw new ArgumentException($"{key} is managed by pinqops deploy/rollback.");
            }
        }

        // A bad port here only surfaces as a failed `up -d` later — and because
        // compose removes the old container before creating the new one, that
        // takes the app down. Catch it while it is still just a form value.
        static void ValidatePortChange(string envFile, string key, string value)
        {
            if (key != SetupTemplates.HostPortVariable && key != SetupTemplates.ContainerPortVariable)
            {
                return;
            }

            if (!int.TryParse(value.Trim(), NumberStyles.None, InvariantCulture, out var port) || !HostPort.IsValid(port))
            {
                throw new ArgumentException($"'{value}' is not a valid port for {key} (1-65535).");
            }

            // The container port is bound inside the container's namespace, and
            // re-saving the current host port would flag the app's own container.
            if (key != SetupTemplates.HostPortVariable
                || EnvFileStore.GetValue(envFile, key) == port.ToString(InvariantCulture))
            {
                return;
            }

            if (!HostPort.IsAvailable(port))
            {
                throw new ArgumentException(
                    $"Port {port} is already in use on this server. Pick a free one — "
                    + "the deploy would fail on 'port is already allocated' and leave the app stopped.");
            }
        }

        foreach (var key in request.Remove ?? [])
        {
            RejectIfDeployManaged(key);
            EnvFileStore.RemoveValue(envFile, key);
        }

        foreach (var (key, value) in request.Set ?? new Dictionary<string, string>())
        {
            RejectIfDeployManaged(key);
            ValidatePortChange(envFile, key, value);
            EnvFileStore.SetValue(envFile, key, value);
        }

        logger.LogWarning("Compose .env edited from the dashboard");
        return new { ok = true };
    }));

// New env only takes effect when the containers are recreated.
app.MapPost("/api/compose/apply", (HttpContext context, UiConfigStore store, IProcessRunner processRunner) =>
    Safe(async () =>
    {
        var composeFile = ResolveApp(store, context).ComposeFile;
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

// ---- Reverse proxy & domains ----------------------------------------------------

app.MapGet("/api/proxy/status", (ProxyService proxy) => Safe(async () => await proxy.StatusAsync()));

app.MapPost("/api/proxy/install", (HttpContext context, ProxyService proxy) =>
    Safe(async () =>
    {
        ProxyInstallRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ProxyInstallRequest>();
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        return await proxy.InstallAsync(request?.AcmeEmail, request?.Staging ?? false, request?.Force ?? false);
    }));

app.MapGet("/api/domains", (UiConfigStore store, ProxyService proxy, DockerService docker) =>
    Safe(async () =>
    {
        var config = proxy.Store.Load();
        var appConfig = store.Current;
        var items = new List<object>();
        foreach (var entry in config.Domains)
        {
            var (_, running) = await docker.ContainerStateAsync(entry.TargetContainer);
            // Drift: the app was renamed or its container port changed, so the
            // stored route now points at the wrong container/port.
            var drift = false;
            try
            {
                var (container, port) = ResolveDomainTarget(appConfig, entry.Target, null);
                drift = container != entry.TargetContainer || port != entry.TargetPort;
            }
            catch (ArgumentException)
            {
                drift = true; // the target app no longer exists
            }

            items.Add(new
            {
                entry.Domain,
                entry.Target,
                entry.TargetContainer,
                entry.TargetPort,
                entry.Enabled,
                running,
                drift,
                url = $"https://{entry.Domain}",
            });
        }

        return new { items };
    }));

app.MapGet("/api/domains/check", (HttpContext context, ProxyService proxy) =>
    Safe(async () => await proxy.CheckDnsAsync(context.Request.Query["domain"].ToString())));

app.MapPost("/api/domains", (HttpContext context, UiConfigStore store, ProxyService proxy) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<DomainRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        var domain = DomainName.Normalize(request.Domain);
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            throw new ArgumentException("A target is required.");
        }

        var (container, port) = ResolveDomainTarget(store.Current, request.Target, request.TargetPort);
        var dns = await proxy.CheckDnsAsync(domain);

        var config = proxy.Store.Load();
        config.Domains.RemoveAll(d => string.Equals(d.Domain, domain, StringComparison.Ordinal));
        config.Domains.Add(new DomainEntry
        {
            Domain = domain,
            Target = request.Target,
            TargetContainer = container,
            TargetPort = port,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        proxy.Store.Save(config);
        await proxy.ApplyAsync();

        logger.LogWarning("Domain {Domain} → {Container}:{Port}", domain, container, port);
        return new { ok = true, domain, dnsMatches = dns.Matches, resolvedIps = dns.ResolvedIps, serverIps = dns.ServerIps };
    }));

app.MapPost("/api/domains/{domain}/toggle", (string domain, ProxyService proxy) =>
    Safe(async () =>
    {
        var normalized = domain.Trim().ToLowerInvariant();
        var config = proxy.Store.Load();
        var entry = config.Domains.FirstOrDefault(d => string.Equals(d.Domain, normalized, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown domain '{domain}'.");
        entry.Enabled = !entry.Enabled;
        proxy.Store.Save(config);
        await proxy.ApplyAsync();
        return new { ok = true, enabled = entry.Enabled };
    }));

app.MapPost("/api/domains/{domain}/delete", (string domain, ProxyService proxy) =>
    Safe(async () =>
    {
        var normalized = domain.Trim().ToLowerInvariant();
        var config = proxy.Store.Load();
        config.Domains.RemoveAll(d => string.Equals(d.Domain, normalized, StringComparison.Ordinal));
        proxy.Store.Save(config);
        await proxy.ApplyAsync();
        return new { ok = true };
    }));

// ---- Backups --------------------------------------------------------------------

app.MapGet("/api/backups", (BackupConfigStore store, BackupService backups, DockerService docker) =>
    Safe(async () =>
    {
        var items = new List<object>();
        foreach (var target in store.Load().Targets)
        {
            var running = target.Kind == "db" && (await docker.ContainerStateAsync(target.Name)).Running;
            items.Add(new
            {
                target.Id, target.Kind, target.Name, target.Engine, target.Schedule,
                target.AtHour, target.RetentionCount, target.Enabled,
                lastRun = backups.LastRun(target.Id),
                running,
                snapshots = backups.ListSnapshots(target.Id),
            });
        }

        return new { items };
    }));

app.MapPost("/api/backups/targets", (HttpContext context, BackupConfigStore store) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<BackupTargetRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Engine)
            || string.IsNullOrWhiteSpace(request.Kind))
        {
            throw new ArgumentException("A source and engine are required.");
        }

        var config = store.Load();
        var id = request.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            var basis = request.Kind == "volume" ? request.Name : request.Engine;
            id = $"{(request.Kind == "volume" ? "vol" : "db")}-{Slugify(basis)}";
        }

        if (!BackupNaming.IsValidId(id))
        {
            throw new ArgumentException("Invalid backup id.");
        }

        var target = config.Targets.FirstOrDefault(t => t.Id == id);
        if (target is null)
        {
            target = new BackupTarget { Id = id };
            config.Targets.Add(target);
        }

        target.Kind = request.Kind;
        target.Name = request.Name;
        target.Engine = request.Engine;
        target.Schedule = request.Schedule is "hourly" or "daily" or "weekly" ? request.Schedule : "daily";
        target.AtHour = Math.Clamp(request.AtHour ?? target.AtHour, 0, 23);
        target.RetentionCount = Math.Clamp(request.RetentionCount ?? target.RetentionCount, 1, 365);
        target.Enabled = request.Enabled ?? target.Enabled;
        store.Save(config);
        return new { ok = true, id };
    }));

app.MapPost("/api/backups/targets/{id}/toggle", (string id, BackupConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var config = store.Load();
        var target = config.Targets.FirstOrDefault(t => t.Id == id)
            ?? throw new ArgumentException($"Unknown backup target '{id}'.");
        target.Enabled = !target.Enabled;
        store.Save(config);
        return new { ok = true, enabled = target.Enabled };
    }));

app.MapDelete("/api/backups/targets/{id}", (string id, BackupConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var config = store.Load();
        config.Targets.RemoveAll(t => t.Id == id);
        store.Save(config);
        return new { ok = true };
    }));

app.MapPost("/api/backups/run/{id}", (string id, BackupConfigStore store, BackupService backups) =>
    Safe(async () =>
    {
        var target = store.Load().Targets.FirstOrDefault(t => t.Id == id)
            ?? throw new ArgumentException($"Unknown backup target '{id}'.");
        return await backups.RunGuardedAsync(target);
    }));

app.MapPost("/api/backups/restore", (HttpContext context, BackupConfigStore store, BackupService backups) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<BackupRestoreRequest>()
            ?? throw new ArgumentException("Invalid request body.");
        var target = store.Load().Targets.FirstOrDefault(t => t.Id == request.TargetId)
            ?? throw new ArgumentException($"Unknown backup target '{request.TargetId}'.");
        await backups.RestoreAsync(target, request.Snapshot ?? "");
        return new { ok = true };
    }));

app.MapDelete("/api/backups/{targetId}/snapshots/{snapshot}", (string targetId, string snapshot, BackupService backups) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        backups.DeleteSnapshot(targetId, snapshot);
        return new { ok = true };
    }));

app.MapGet("/api/backups/download", (HttpContext context, BackupService backups) =>
{
    var targetId = context.Request.Query["target"].ToString();
    var snapshot = context.Request.Query["snapshot"].ToString();
    var path = backups.SnapshotPath(targetId, snapshot);
    return path is null
        ? Error(404, "Snapshot not found.")
        : Results.File(path, "application/octet-stream", snapshot);
});

// ---- API tokens -----------------------------------------------------------------

app.MapGet("/api/tokens", (ApiTokenStore tokens) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        return new
        {
            items = tokens.List().Select(t => new
            {
                t.Id, t.Name, t.Scope, t.Last4, t.CreatedAt, t.LastUsedAt,
            }),
        };
    }));

app.MapPost("/api/tokens", (HttpContext context, ApiTokenStore tokens) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<TokenCreateRequest>();
        var scope = request?.Scope is "read" or "deploy" or "admin" ? request.Scope : "read";
        var (token, plaintext) = tokens.Create(request?.Name ?? "token", scope, DateTimeOffset.UtcNow);
        logger.LogWarning("API token '{Name}' created with scope {Scope}", token.Name, token.Scope);
        // The plaintext is returned exactly once, here.
        return new { ok = true, id = token.Id, token = plaintext, scope = token.Scope };
    }));

app.MapDelete("/api/tokens/{id}", (string id, ApiTokenStore tokens) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        tokens.Delete(id);
        return new { ok = true };
    }));

// ---- Notifications --------------------------------------------------------------

app.MapGet("/api/notifications", (HttpContext context, UiConfigStore store) =>
    Safe(async () =>
    {
        await Task.CompletedTask;
        var config = new PinqOps.Notifications.NotificationConfigStore(ResolveApp(store, context).ComposeFile).Load();
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

        var configStore = new PinqOps.Notifications.NotificationConfigStore(ResolveApp(store, context).ComposeFile);
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

        using var dispatcher = new PinqOps.Notifications.NotificationDispatcher(ResolveApp(store, context).ComposeFile);
        var delivered = await dispatcher.SendTestAsync(channel);
        return new { ok = delivered, delivered };
    }));

// ---- Local runner & system ------------------------------------------------------

app.MapGet("/api/runner/local", (HttpContext context, UiConfigStore store, LocalRunnerService runner) =>
    Safe(async () => await runner.GetStatusAsync(ResolveApp(store, context).RunnerDirectory)));

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

/// <summary>Lowercases and reduces a name to a safe id fragment ([a-z0-9._-]).</summary>
static string Slugify(string value)
{
    var kept = value.ToLowerInvariant()
        .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-')
        .ToArray();
    var slug = new string(kept).Trim('-', '.', '_');
    return slug.Length > 0 ? slug : "target";
}

/// <summary>The container name and port a domain target resolves to. Target is
/// an app id (→ that app's compose container) or "catalog:&lt;id&gt;" (→ a catalog
/// container). An optional requested port overrides the default.</summary>
static (string Container, int Port) ResolveDomainTarget(UiConfig config, string target, int? requestedPort)
{
    if (target.StartsWith("catalog:", StringComparison.Ordinal))
    {
        var id = target["catalog:".Length..];
        var spec = AppCatalog.Find(id) ?? throw new ArgumentException($"Unknown app '{id}'.");
        var catalogPort = requestedPort
            ?? (spec.Ports.Length > 0 ? spec.Ports[0].Container : throw new ArgumentException("This app exposes no port to route to."));
        return ($"{AppCatalog.ContainerPrefix}{id}", catalogPort);
    }

    var connection = config.Apps.FirstOrDefault(a => string.Equals(a.Id, target, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown app '{target}'.");
    var repository = GitHubRepositoryParser.Parse(connection.RepoUrl);
    var container = $"{ComposeProjectName.FromRepository(repository.Name)}-app-1";
    var envPort = TryParsePort(
        EnvFileStore.GetValue(PinqOpsStatePaths.EnvFile(connection.ComposeFile), SetupTemplates.ContainerPortVariable));
    return (container, requestedPort ?? envPort ?? DockerfileInspector.DefaultPort);
}

/// <summary>Whether <paramref name="path"/> is a build manifest directly in the
/// directory identified by <paramref name="prefix"/> (no deeper).</summary>
static bool IsDirectManifest(string path, string prefix)
{
    if (!path.StartsWith(prefix, StringComparison.Ordinal))
    {
        return false;
    }

    var name = path[prefix.Length..];
    if (name.Contains('/'))
    {
        return false;
    }

    return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || name is "package.json" or "go.mod" or "Cargo.toml"
            or "requirements.txt" or "pyproject.toml" or "composer.json" or "Gemfile";
}

/// <summary>The app a request targets: ?appId=… or the sole/first app.</summary>
static AppConnection ResolveApp(UiConfigStore store, HttpContext context) =>
    AppResolver.Resolve(store.Current, context.Request.Query["appId"].ToString());

/// <summary>A stored .env port value as an int, or null when absent/garbage.</summary>
static int? TryParsePort(string? value) =>
    int.TryParse(value?.Trim(), NumberStyles.None, InvariantCulture, out var port) && HostPort.IsValid(port)
        ? port
        : null;

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
