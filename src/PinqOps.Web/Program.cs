using PinqOps;
using PinqOps.Web;

// pinqops-ui — the optional web dashboard for a pinqops server.
// Binds 7467 by default ("PINQ" on a phone keypad) — an otherwise unassigned port.

var port = GetOption(args, "--port") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_PORT") ?? "7467";
var host = GetOption(args, "--host") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_HOST") ?? "0.0.0.0";

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://{host}:{port}");

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<UiConfigStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<GitHubDashboardService>();
builder.Services.AddSingleton<LocalRunnerService>();
builder.Services.AddSingleton<SystemInfoService>();

var app = builder.Build();
var deployGate = new SemaphoreSlim(1, 1);

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

app.MapGet("/", () => Results.Stream(OpenIndexHtml(), "text/html; charset=utf-8"));

// ---- Auth ------------------------------------------------------------------

app.MapGet("/api/auth/state", (UiConfigStore store, GitHubDashboardService gitHub) => Results.Json(new
{
    needsSetup = string.IsNullOrEmpty(store.Current.PasswordHash),
    githubConfigured = gitHub.IsConfigured,
}));

app.MapPost("/api/auth/setup", async (HttpContext context, UiConfigStore store, SessionStore sessions) =>
{
    var request = await context.Request.ReadFromJsonAsync<PasswordRequest>();
    if (request?.Password is not { Length: >= 8 } password)
    {
        return Error(400, "Choose a password of at least 8 characters.");
    }

    if (!string.IsNullOrEmpty(store.Current.PasswordHash))
    {
        return Error(409, "A password is already set — log in instead.");
    }

    store.Update(config => config.PasswordHash = PasswordHasher.Hash(password));
    return Results.Json(new { token = sessions.Create() });
});

app.MapPost("/api/auth/login", async (HttpContext context, UiConfigStore store, SessionStore sessions) =>
{
    var request = await context.Request.ReadFromJsonAsync<PasswordRequest>();
    var hash = store.Current.PasswordHash;
    if (hash is null)
    {
        return Error(409, "No password set yet — create one first.");
    }

    if (request?.Password is not { } password || !PasswordHasher.Verify(password, hash))
    {
        await Task.Delay(500); // blunt brute-force throttle
        return Error(401, "Wrong password.");
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

app.MapPost("/api/auth/change-password", async (HttpContext context, UiConfigStore store) =>
{
    var request = await context.Request.ReadFromJsonAsync<ChangePasswordRequest>();
    var hash = store.Current.PasswordHash;
    if (hash is null || request?.CurrentPassword is not { } current || !PasswordHasher.Verify(current, hash))
    {
        return Error(401, "Current password is wrong.");
    }

    if (request.NewPassword is not { Length: >= 8 } fresh)
    {
        return Error(400, "New password must be at least 8 characters.");
    }

    store.Update(config => config.PasswordHash = PasswordHasher.Hash(fresh));
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
            fullName = repo.TryGetProperty("full_name", out var name) ? name.GetString() : repository.ToString(),
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

Console.WriteLine($"pinqops-ui listening on http://{host}:{port}");
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

static string? ReadBearerToken(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
}

static Stream OpenIndexHtml() =>
    typeof(Program).Assembly.GetManifestResourceStream("index.html")
    ?? throw new InvalidOperationException("Embedded dashboard page is missing.");

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
