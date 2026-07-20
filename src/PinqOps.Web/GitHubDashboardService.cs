using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Read-only GitHub API access for the dashboard: repository info, self-hosted
/// runners, and workflow runs. Credentials come from the stored
/// <see cref="UiConfig"/> — a PAT as Bearer, or username + token as Basic auth.
/// The token is only ever placed in the Authorization header.
/// </summary>
public sealed class GitHubDashboardService : IDisposable
{
    private const string ApiVersion = "2022-11-28";
    private const string PublicHost = "github.com";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly UiConfigStore _configStore;

    public GitHubDashboardService(UiConfigStore configStore, HttpClient? httpClient = null)
    {
        _configStore = configStore;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _ownsClient = httpClient is null;
    }

    public bool IsConfigured
    {
        get
        {
            var config = _configStore.Current;
            return !string.IsNullOrWhiteSpace(config.RepoUrl) && !string.IsNullOrWhiteSpace(config.Pat);
        }
    }

    /// <summary>Validates a candidate connection by fetching the repository.</summary>
    public async Task<JsonElement> TestConnectionAsync(string repoUrl, string? username, string pat)
    {
        var repository = GitHubRepositoryParser.Parse(repoUrl);
        return await GetAsync(repository, Credentials(username, pat), $"/repos/{repository.Owner}/{repository.Name}")
            .ConfigureAwait(false);
    }

    /// <summary>The identity behind a token (works before a repository is chosen).</summary>
    public async Task<object> GetUserAsync(string? username = null, string? pat = null)
    {
        var auth = TokenAuth(username, pat);
        var user = await GetAsync(null, auth, "/user").ConfigureAwait(false);
        return new
        {
            login = GetString(user, "login"),
            name = GetString(user, "name"),
            avatarUrl = GetString(user, "avatar_url"),
            htmlUrl = GetString(user, "html_url"),
        };
    }

    /// <summary>
    /// Repositories the stored token can reach, so the user can pick one
    /// instead of typing a URL. Sorted by recent push; capped at 200.
    /// </summary>
    public async Task<List<object>> GetReposAsync()
    {
        var auth = TokenAuth(null, null);
        var result = new List<object>();
        for (var page = 1; page <= 2; page++)
        {
            var repos = await GetAsync(
                    null, auth,
                    $"/user/repos?per_page=100&page={page}&sort=pushed&affiliation=owner,collaborator,organization_member")
                .ConfigureAwait(false);
            if (repos.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var repo in repos.EnumerateArray())
            {
                count++;
                var permissions = repo.TryGetProperty("permissions", out var perms) ? perms : default;
                result.Add(new
                {
                    fullName = GetString(repo, "full_name"),
                    htmlUrl = GetString(repo, "html_url"),
                    isPrivate = repo.TryGetProperty("private", out var p) && p.GetBoolean(),
                    pushedAt = GetString(repo, "pushed_at"),
                    admin = permissions.ValueKind == JsonValueKind.Object
                            && permissions.TryGetProperty("admin", out var a) && a.GetBoolean(),
                    push = permissions.ValueKind == JsonValueKind.Object
                           && permissions.TryGetProperty("push", out var w) && w.GetBoolean(),
                });
            }

            if (count < 100)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Readiness check for the connected repository: does it have the files the
    /// pipeline needs (Dockerfile, deploy workflow)?
    /// </summary>
    public async Task<object> CheckRepoSetupAsync()
    {
        var (repository, auth) = Context();
        var basePath = $"/repos/{repository.Owner}/{repository.Name}";
        var repo = await GetAsync(repository, auth, basePath).ConfigureAwait(false);

        var dockerfileTask = ContentExistsAsync(repository, auth, "Dockerfile");
        var workflowTask = ContentExistsAsync(repository, auth, ".github/workflows/deploy.yml");
        await Task.WhenAll(dockerfileTask, workflowTask).ConfigureAwait(false);

        return new
        {
            fullName = GetString(repo, "full_name"),
            defaultBranch = GetString(repo, "default_branch"),
            hasDockerfile = dockerfileTask.Result,
            hasWorkflow = workflowTask.Result,
        };
    }

    /// <summary>Commits the deploy workflow into the connected repository.</summary>
    public async Task<object> CreateWorkflowFileAsync(string yamlContent)
    {
        var (repository, auth) = Context();
        var path = $"/repos/{repository.Owner}/{repository.Name}/contents/.github/workflows/deploy.yml";
        var body = JsonSerializer.Serialize(new
        {
            message = "ci: add pinqops deploy workflow (generated by pinqops-ui)",
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(yamlContent)),
        });

        var response = await SendAsync(repository, auth, HttpMethod.Put, path, body).ConfigureAwait(false);
        var commitUrl = response.TryGetProperty("commit", out var commit) ? GetString(commit, "html_url") : null;
        return new { ok = true, commitUrl };
    }

    /// <summary>Just the repository's self-hosted runners (for the setup check).</summary>
    public async Task<(int Online, int Total)> GetRunnersSummaryAsync()
    {
        var (repository, auth) = Context();
        var payload = await GetAsync(
                repository, auth, $"/repos/{repository.Owner}/{repository.Name}/actions/runners?per_page=100")
            .ConfigureAwait(false);
        var runners = TrimRunners(payload);
        return (runners.Count(r => r.Status == "online"), runners.Count);
    }

    /// <summary>
    /// The port the connected repository's Dockerfile declares with
    /// <c>EXPOSE</c>, or null when there is no Dockerfile and no usable EXPOSE.
    /// </summary>
    /// <remarks>
    /// Only "there is no answer" outcomes are folded into null. Transport
    /// failures propagate so the caller can log them and decide — this is a hint,
    /// and callers fall back to a default rather than fail.
    /// </remarks>
    public async Task<int?> GetDockerfileExposedPortAsync()
    {
        var (repository, auth) = Context();

        JsonElement payload;
        try
        {
            payload = await GetAsync(
                    repository, auth, $"/repos/{repository.Owner}/{repository.Name}/contents/Dockerfile")
                .ConfigureAwait(false);
        }
        catch (GitHubApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }

        // The contents API returns base64 with embedded newlines; files over
        // ~1 MB come back with an empty content field instead.
        var encoded = GetString(payload, "content");
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            var dockerfile = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\n", string.Empty)));
            return DockerfileInspector.FindExposedPort(dockerfile);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<bool> ContentExistsAsync(GitHubRepository repository, AuthenticationHeaderValue auth, string filePath)
    {
        try
        {
            await GetAsync(repository, auth, $"/repos/{repository.Owner}/{repository.Name}/contents/{filePath}")
                .ConfigureAwait(false);
            return true;
        }
        catch (GitHubApiException exception) when (exception.StatusCode == 404)
        {
            return false;
        }
    }

    /// <summary>Auth from an explicit candidate token, falling back to the stored one.</summary>
    private AuthenticationHeaderValue TokenAuth(string? username, string? pat)
    {
        if (string.IsNullOrWhiteSpace(pat))
        {
            var config = _configStore.Current;
            (username, pat) = (config.Username, config.Pat);
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException("GitHub is not connected yet — add the repository and token in Settings.");
        }

        return Credentials(username, pat);
    }

    /// <summary>
    /// Everything the dashboard shows about GitHub in one call: the repository,
    /// its self-hosted runners, recent workflow runs, and the most recent job
    /// that actually executed on one of those runners ("when did the runner
    /// last run").
    /// </summary>
    public async Task<object> GetOverviewAsync(int runCount = 20)
    {
        var (repository, auth) = Context();
        var basePath = $"/repos/{repository.Owner}/{repository.Name}";

        var repoTask = GetAsync(repository, auth, basePath);
        var runnersTask = GetAsync(repository, auth, $"{basePath}/actions/runners?per_page=100");
        var runsTask = GetAsync(repository, auth, $"{basePath}/actions/runs?per_page={runCount}");
        await Task.WhenAll(repoTask, runnersTask, runsTask).ConfigureAwait(false);

        var repo = repoTask.Result;
        List<RunnerSummary> runners = TrimRunners(runnersTask.Result);
        var runs = TrimRuns(runsTask.Result);
        var lastRunnerJob = await FindLastSelfHostedJobAsync(repository, auth, runsTask.Result, runners)
            .ConfigureAwait(false);

        return new
        {
            repo = new
            {
                fullName = GetString(repo, "full_name"),
                description = GetString(repo, "description"),
                htmlUrl = GetString(repo, "html_url"),
                defaultBranch = GetString(repo, "default_branch"),
                isPrivate = repo.TryGetProperty("private", out var p) && p.GetBoolean(),
                pushedAt = GetString(repo, "pushed_at"),
            },
            runners,
            runs,
            lastRunnerJob,
        };
    }

    /// <summary>
    /// Walks the most recent runs' jobs (bounded) and returns the newest job
    /// that executed on one of the repository's self-hosted runners.
    /// </summary>
    private async Task<object?> FindLastSelfHostedJobAsync(
        GitHubRepository repository,
        AuthenticationHeaderValue auth,
        JsonElement runsPayload,
        List<RunnerSummary> runners)
    {
        var runnerNames = runners
            .Select(runner => runner.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!runsPayload.TryGetProperty("workflow_runs", out var runs))
        {
            return null;
        }

        var inspected = 0;
        foreach (var run in runs.EnumerateArray())
        {
            if (inspected >= 6)
            {
                break;
            }

            inspected++;
            var runId = run.GetProperty("id").GetInt64();
            JsonElement jobsPayload;
            try
            {
                jobsPayload = await GetAsync(
                        repository, auth,
                        $"/repos/{repository.Owner}/{repository.Name}/actions/runs/{runId}/jobs?per_page=30")
                    .ConfigureAwait(false);
            }
            catch (GitHubApiException)
            {
                continue;
            }

            if (!jobsPayload.TryGetProperty("jobs", out var jobs))
            {
                continue;
            }

            // Runs are returned newest-first, so the first match is the answer.
            foreach (var job in jobs.EnumerateArray())
            {
                var runnerName = GetString(job, "runner_name");
                var labels = job.TryGetProperty("labels", out var labelsElement)
                    ? labelsElement.EnumerateArray().Select(l => l.GetString() ?? "").ToArray()
                    : [];

                var isSelfHosted = labels.Contains("self-hosted")
                    || (!string.IsNullOrEmpty(runnerName) && runnerNames.Contains(runnerName));
                if (!isSelfHosted)
                {
                    continue;
                }

                return new
                {
                    runId,
                    workflowName = GetString(run, "name"),
                    runNumber = run.TryGetProperty("run_number", out var n) ? n.GetInt32() : 0,
                    jobName = GetString(job, "name"),
                    runnerName,
                    labels,
                    status = GetString(job, "status"),
                    conclusion = GetString(job, "conclusion"),
                    startedAt = GetString(job, "started_at"),
                    completedAt = GetString(job, "completed_at"),
                    htmlUrl = GetString(job, "html_url"),
                };
            }
        }

        return null;
    }

    internal sealed record RunnerSummary(
        long Id, string Name, string? Os, string? Status, bool Busy, string?[] Labels);

    private static List<RunnerSummary> TrimRunners(JsonElement payload)
    {
        var result = new List<RunnerSummary>();
        if (!payload.TryGetProperty("runners", out var runners))
        {
            return result;
        }

        foreach (var runner in runners.EnumerateArray())
        {
            result.Add(new RunnerSummary(
                runner.GetProperty("id").GetInt64(),
                GetString(runner, "name") ?? "",
                GetString(runner, "os"),
                GetString(runner, "status"),
                runner.TryGetProperty("busy", out var busy) && busy.GetBoolean(),
                runner.TryGetProperty("labels", out var labels)
                    ? labels.EnumerateArray().Select(l => GetString(l, "name")).ToArray()
                    : []));
        }

        return result;
    }

    private static List<object> TrimRuns(JsonElement payload)
    {
        var result = new List<object>();
        if (!payload.TryGetProperty("workflow_runs", out var runs))
        {
            return result;
        }

        foreach (var run in runs.EnumerateArray())
        {
            result.Add(new
            {
                id = run.GetProperty("id").GetInt64(),
                runNumber = run.TryGetProperty("run_number", out var n) ? n.GetInt32() : 0,
                workflowName = GetString(run, "name"),
                displayTitle = GetString(run, "display_title"),
                @event = GetString(run, "event"),
                status = GetString(run, "status"),
                conclusion = GetString(run, "conclusion"),
                branch = GetString(run, "head_branch"),
                sha = GetString(run, "head_sha") is { Length: >= 7 } sha ? sha[..7] : null,
                actor = run.TryGetProperty("actor", out var actor) ? GetString(actor, "login") : null,
                createdAt = GetString(run, "created_at"),
                updatedAt = GetString(run, "updated_at"),
                runStartedAt = GetString(run, "run_started_at"),
                htmlUrl = GetString(run, "html_url"),
            });
        }

        return result;
    }

    private (GitHubRepository Repository, AuthenticationHeaderValue Auth) Context()
    {
        var config = _configStore.Current;
        if (string.IsNullOrWhiteSpace(config.RepoUrl) || string.IsNullOrWhiteSpace(config.Pat))
        {
            throw new InvalidOperationException("GitHub is not connected yet — add the repository and token in Settings.");
        }

        return (GitHubRepositoryParser.Parse(config.RepoUrl), Credentials(config.Username, config.Pat));
    }

    private static AuthenticationHeaderValue Credentials(string? username, string pat) =>
        string.IsNullOrWhiteSpace(username)
            ? new AuthenticationHeaderValue("Bearer", pat)
            : new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{pat}")));

    private Task<JsonElement> GetAsync(
        GitHubRepository? repository,
        AuthenticationHeaderValue auth,
        string path) =>
        SendAsync(repository, auth, HttpMethod.Get, path, jsonBody: null);

    /// <summary>
    /// API base for calls that have no repository yet (user, repo list): honor
    /// the configured repository's host when one is stored so GitHub
    /// Enterprise setups keep working; otherwise public GitHub.
    /// </summary>
    private string DefaultApiBase()
    {
        var repoUrl = _configStore.Current.RepoUrl;
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            try
            {
                return ApiBase(GitHubRepositoryParser.Parse(repoUrl));
            }
            catch (ArgumentException)
            {
            }
        }

        return "https://api.github.com";
    }

    private async Task<JsonElement> SendAsync(
        GitHubRepository? repository,
        AuthenticationHeaderValue auth,
        HttpMethod method,
        string path,
        string? jsonBody)
    {
        var apiBase = repository is null ? DefaultApiBase() : ApiBase(repository);
        using var request = new HttpRequestMessage(method, apiBase + path);
        request.Headers.Authorization = auth;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("pinqops-ui");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadMessage(body);
            throw new GitHubApiException(
                (int)response.StatusCode,
                $"GitHub API request failed ({(int)response.StatusCode}) for {path}"
                + (string.IsNullOrWhiteSpace(message) ? "." : $": {message}"));
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string ApiBase(GitHubRepository repository) =>
        string.Equals(repository.Host, PublicHost, StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : $"https://{repository.Host}/api/v3";

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
