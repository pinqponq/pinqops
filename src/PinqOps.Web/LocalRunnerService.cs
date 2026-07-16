using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Inspects the self-hosted runner installed on this machine: its systemd
/// service state and its <c>_diag</c> logs (whose newest Worker log timestamp is
/// the last time the runner actually executed a job locally).
/// </summary>
public sealed class LocalRunnerService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _processRunner;

    public LocalRunnerService(IProcessRunner processRunner) => _processRunner = processRunner;

    public async Task<object> GetStatusAsync(string runnerDirectory)
    {
        var installed = Directory.Exists(runnerDirectory)
            && File.Exists(Path.Combine(runnerDirectory, "config.sh"));

        string? agentName = null;
        string? gitHubUrl = null;
        var runnerFile = Path.Combine(runnerDirectory, ".runner");
        if (File.Exists(runnerFile))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runnerFile).ConfigureAwait(false));
                agentName = GetString(document.RootElement, "agentName");
                gitHubUrl = GetString(document.RootElement, "gitHubUrl") ?? GetString(document.RootElement, "serverUrl");
            }
            catch (JsonException)
            {
                // A malformed .runner file just means less detail on the dashboard.
            }
        }

        var diagDirectory = Path.Combine(runnerDirectory, "_diag");
        var lastWorkerLogAt = LatestWriteTime(diagDirectory, "Worker_*.log");
        var lastRunnerLogAt = LatestWriteTime(diagDirectory, "Runner_*.log");

        return new
        {
            installed,
            runnerDirectory,
            agentName,
            gitHubUrl,
            service = await GetServiceStatusAsync().ConfigureAwait(false),
            lastWorkerLogAt,
            lastRunnerLogAt,
        };
    }

    private async Task<object?> GetServiceStatusAsync()
    {
        var list = await RunAsync(
                "systemctl", "list-units", "--all", "--plain", "--no-legend", "--no-pager",
                "actions.runner.*")
            .ConfigureAwait(false);
        if (list is null || !list.Succeeded)
        {
            return null;
        }

        var unit = list.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .FirstOrDefault(name => name is not null && name.StartsWith("actions.runner.", StringComparison.Ordinal));
        if (unit is null)
        {
            return null;
        }

        var show = await RunAsync(
                "systemctl", "show", unit,
                "--property=ActiveState", "--property=SubState", "--property=ExecMainStartTimestamp")
            .ConfigureAwait(false);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (show is not null && show.Succeeded)
        {
            foreach (var line in show.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    properties[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }
        }

        return new
        {
            unit,
            activeState = properties.GetValueOrDefault("ActiveState"),
            subState = properties.GetValueOrDefault("SubState"),
            since = properties.GetValueOrDefault("ExecMainStartTimestamp"),
        };
    }

    private async Task<ProcessResult?> RunAsync(string fileName, params string[] arguments)
    {
        try
        {
            using var cts = new CancellationTokenSource(CommandTimeout);
            return await _processRunner.RunAsync(fileName, arguments, workingDirectory: null, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // systemctl may be missing entirely (containers, macOS); report "unknown".
            return null;
        }
    }

    private static DateTimeOffset? LatestWriteTime(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            var writeTime = File.GetLastWriteTimeUtc(file);
            if (latest is null || writeTime > latest)
            {
                latest = writeTime;
            }
        }

        return latest;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
