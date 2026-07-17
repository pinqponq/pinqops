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
builder.Services.AddSingleton<LocalRunnerService>();
builder.Services.AddSingleton<SystemInfoService>();

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
var deployGate = new SemaphoreSlim(1, 1);

// The dashboard page is one embedded file; cache its bytes and pin its inline
// script with a CSP hash so no other script can ever execute on the page.
var indexBytes = LoadIndexBytes();
var contentSecurityPolicy =
    $"default-src 'none'; script-src '{HashInlineScript(indexBytes)}'; style-src 'unsafe-inline'; "
    + "img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

// First-run bootstrap secret: creating the dashboard password requires this
// code from the server console, so whoever reaches the port first cannot
// claim an unconfigured dashboard.
var setupCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

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

app.MapPost("/api/auth/setup", async (HttpContext context, UiConfigStore store, SessionStore sessions) =>
{
    var request = await context.Request.ReadFromJsonAsync<SetupRequest>();
    if (!string.IsNullOrEmpty(store.Current.PasswordHash))
    {
        return Error(409, "A password is already set — log in instead.");
    }

    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request?.SetupCode?.Trim().ToLowerInvariant() ?? ""),
            Encoding.UTF8.GetBytes(setupCode)))
    {
        logger.LogWarning("Dashboard setup attempt with a wrong setup code from {Client}", ClientKey(context));
        await Task.Delay(500);
        return Error(401, "Wrong setup code — it is printed on the server console where pinqops-ui runs.");
    }

    if (request?.Password is not { Length: >= 8 } password)
    {
        return Error(400, "Choose a password of at least 8 characters.");
    }

    store.Update(config => config.PasswordHash = PasswordHasher.Hash(password));
    logger.LogWarning("Dashboard password created from {Client}", ClientKey(context));
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
    return Results.Json(new
    {
        repoUrl = config.RepoUrl,
        username = config.Username,
        patMasked = config.Pat is { Length: > 4 } pat ? $"••••••••{pat[^4..]}" : null,
        composeFile = config.ComposeFile,
        runnerDirectory = config.RunnerDirectory,
        configPath = store.Path_,
        lastDeploy = config.LastDeploy,
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

        // Validate the connection before saving anything.
        var repo = await gitHub.TestConnectionAsync(request.RepoUrl, request.Username, pat);

        store.Update(config =>
        {
            config.RepoUrl = repository.ToUrl();
            config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            config.Pat = pat;
            if (!string.IsNullOrWhiteSpace(request.ComposeFile))
            {
                config.ComposeFile = request.ComposeFile.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.RunnerDirectory))
            {
                config.RunnerDirectory = request.RunnerDirectory.Trim();
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

app.MapPost("/api/docker/login", (HttpContext context, DockerService docker) =>
    Safe(async () =>
    {
        var request = await context.Request.ReadFromJsonAsync<RegistryLoginRequest>();
        var output = await docker.LoginAsync(
            string.IsNullOrWhiteSpace(request?.Registry) ? "ghcr.io" : request.Registry.Trim(),
            request?.Username ?? "",
            request?.Token ?? "");
        return new { ok = true, output };
    }));

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

// ---- Deploy --------------------------------------------------------------------

app.MapPost("/api/deploy", async (UiConfigStore store, IProcessRunner processRunner) =>
{
    if (!await deployGate.WaitAsync(0))
    {
        return Error(409, "A deploy is already in progress.");
    }

    try
    {
        var lines = new List<string>();
        var deployer = new Deployer(processRunner, lines.Add);
        var succeeded = await deployer.DeployAsync(DeployOptions.Create(store.Current.ComposeFile));
        store.Update(config => config.LastDeploy = new LastDeployInfo(DateTimeOffset.UtcNow, succeeded));
        return Results.Json(new { succeeded, log = string.Join('\n', lines) });
    }
    catch (Exception exception)
    {
        return Error(500, exception.Message);
    }
    finally
    {
        deployGate.Release();
    }
});

// ---- Local runner & system ------------------------------------------------------

app.MapGet("/api/runner/local", (UiConfigStore store, LocalRunnerService runner) =>
    Safe(async () => await runner.GetStatusAsync(store.Current.RunnerDirectory)));

app.MapGet("/api/system", (SystemInfoService system) => Results.Json(system.GetInfo()));

Console.WriteLine($"pinqops-ui listening on {(useTls ? "https" : "http")}://{host}:{port}");
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
