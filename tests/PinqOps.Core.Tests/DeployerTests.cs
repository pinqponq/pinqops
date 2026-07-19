using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class DeployerTests : IDisposable
{
    private readonly string _projectDirectory;
    private readonly string _composePath;

    private const string HealthyPs = """{"Name":"app","State":"running","Health":"healthy"}""";

    public DeployerTests()
    {
        _projectDirectory = Directory.CreateTempSubdirectory("pinqops-deployer-tests").FullName;
        _composePath = Path.Combine(_projectDirectory, "docker-compose.yml");
        File.WriteAllText(_composePath, "services: {}\n");
    }

    public void Dispose() => Directory.Delete(_projectDirectory, recursive: true);

    private static FakeProcessRunner ScriptedRunner(string psOutput = HealthyPs, int pullExit = 0, int upExit = 0) =>
        new((_, arguments) =>
        {
            if (arguments.Contains("ps"))
            {
                return new ProcessResult(0, psOutput, string.Empty);
            }

            if (arguments.Contains("pull"))
            {
                return new ProcessResult(pullExit, string.Empty, pullExit == 0 ? string.Empty : "boom");
            }

            if (arguments.Contains("up"))
            {
                return new ProcessResult(upExit, string.Empty, upExit == 0 ? string.Empty : "boom");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        });

    private DeployOptions Options(
        string? tag = null,
        bool pruneImages = true,
        TimeSpan? healthCheckTimeout = null,
        string trigger = DeployRecordValues.TriggerManual) =>
        DeployOptions.Create(
            _composePath,
            pruneImages: pruneImages,
            tag: tag,
            healthCheckTimeout: healthCheckTimeout ?? TimeSpan.FromSeconds(5),
            trigger: trigger);

    [Fact]
    public async Task DeployAsync_HappyPath_RunsPullUpHealthRetention_InOrder()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.True(result);
        var commands = runner.Invocations.Select(invocation => invocation.CommandLine).ToList();
        Assert.Equal($"docker compose -f {_composePath} pull", commands[0]);
        Assert.Equal($"docker compose -f {_composePath} up -d", commands[1]);
        Assert.Equal($"docker compose -f {_composePath} ps -a --format json", commands[2]);
        Assert.Equal($"docker compose -f {_composePath} config --images", commands[3]);
        Assert.Equal("docker image prune -f", commands[^1]);
    }

    [Fact]
    public async Task DeployAsync_PullFails_SkipsUp_ReturnsFalse()
    {
        var runner = ScriptedRunner(pullExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.False(result);
        Assert.Single(runner.Invocations);
        Assert.Contains("pull", runner.Invocations[0].Arguments);
    }

    [Fact]
    public async Task DeployAsync_UpFails_SkipsHealthAndPrune_ReturnsFalse()
    {
        var runner = ScriptedRunner(upExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options());

        Assert.False(result);
        Assert.Equal(2, runner.Invocations.Count); // pull + up only
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_PruneDisabled_DoesNotPrune()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(pruneImages: false));

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task DeployAsync_HealthCheckDisabled_SkipsComposePs()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(healthCheckTimeout: TimeSpan.Zero));

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("ps"));
    }

    [Fact]
    public async Task DeployAsync_TagProvided_PinsEnvBeforePull()
    {
        string? tagAtPullTime = null;
        var envFile = Path.Combine(_projectDirectory, ".env");
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("pull"))
            {
                tagAtPullTime = EnvFileStore.GetValue(envFile, Deployer.TagVariable);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123"));

        Assert.True(result);
        Assert.Equal("sha-abc123", tagAtPullTime);
        Assert.Equal("sha-abc123", EnvFileStore.GetValue(envFile, Deployer.TagVariable));
    }

    [Fact]
    public async Task DeployAsync_PullFails_RestoresPreviousTag()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-old");
        var runner = ScriptedRunner(pullExit: 1);
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-new"));

        Assert.False(result);
        Assert.Equal("sha-old", EnvFileStore.GetValue(envFile, Deployer.TagVariable));
    }

    [Fact]
    public async Task DeployAsync_HealthCheckFails_RecordsFailed_NoPrune_KeepsNewTag()
    {
        var envFile = Path.Combine(_projectDirectory, ".env");
        EnvFileStore.SetValue(envFile, Deployer.TagVariable, "sha-old");
        var runner = ScriptedRunner(psOutput: """{"Name":"app","State":"exited","Health":""}""");
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-new"));

        Assert.False(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("prune"));
        // The new (unhealthy) version IS what is running; .env must reflect it.
        Assert.Equal("sha-new", EnvFileStore.GetValue(envFile, Deployer.TagVariable));

        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultFailed, record.Result);
        Assert.Equal(DeployRecordValues.HealthFailed, record.HealthCheck);
        Assert.Equal("sha-new", record.Tag);
        Assert.Equal("sha-old", record.PreviousTag);
    }

    [Fact]
    public async Task DeployAsync_Success_RecordsHistoryAndNotifiesObserver()
    {
        var runner = ScriptedRunner();
        var history = new DeployHistoryStore(_composePath);
        var observer = new RecordingObserver();
        var deployer = new Deployer(runner, history: history, observer: observer);

        var result = await deployer.DeployAsync(Options(tag: "sha-abc123", trigger: DeployRecordValues.TriggerCi));

        Assert.True(result);
        var record = Assert.Single(history.Load());
        Assert.Equal(DeployRecordValues.ResultSucceeded, record.Result);
        Assert.Equal(DeployRecordValues.TriggerCi, record.Trigger);
        Assert.Equal(DeployRecordValues.HealthPassed, record.HealthCheck);

        var outcome = Assert.Single(observer.Outcomes);
        Assert.Equal(DeployRecordValues.ResultSucceeded, outcome.Result);
        Assert.Equal("sha-abc123", outcome.Tag);
    }

    [Fact]
    public async Task DeployAsync_ObserverThrows_DoesNotFailDeploy()
    {
        var runner = ScriptedRunner();
        var deployer = new Deployer(runner, observer: new ThrowingObserver());

        var result = await deployer.DeployAsync(Options());

        Assert.True(result);
    }

    [Fact]
    public async Task DeployAsync_Rollback_ImageLocal_SkipsPull()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/o/r:sha-old\n", string.Empty);
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var history = new DeployHistoryStore(_composePath);
        var deployer = new Deployer(runner, history: history);

        var result = await deployer.DeployAsync(Options(tag: "sha-old", trigger: DeployRecordValues.TriggerRollback));

        Assert.True(result);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.Arguments.Contains("inspect") && invocation.Arguments.Contains("ghcr.io/o/r:sha-old"));
        Assert.Equal(DeployRecordValues.ResultRolledBack, Assert.Single(history.Load()).Result);
    }

    [Fact]
    public async Task DeployAsync_Rollback_ImageMissing_FallsBackToPull()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, "ghcr.io/o/r:sha-old\n", string.Empty);
            }

            if (arguments.Contains("inspect"))
            {
                return new ProcessResult(1, string.Empty, "No such image");
            }

            return arguments.Contains("ps")
                ? new ProcessResult(0, HealthyPs, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });
        var deployer = new Deployer(runner);

        var result = await deployer.DeployAsync(Options(tag: "sha-old", trigger: DeployRecordValues.TriggerRollback));

        Assert.True(result);
        Assert.Contains(runner.Invocations, invocation => invocation.Arguments.Contains("pull"));
    }

    private sealed class RecordingObserver : IDeployObserver
    {
        public List<DeployOutcome> Outcomes { get; } = new();

        public Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IDeployObserver
    {
        public Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("notifier down");
    }
}
