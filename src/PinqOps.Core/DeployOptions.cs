using System.Text.RegularExpressions;

namespace PinqOps;

/// <summary>
/// Options for a single deployment. The compose file path is fixed on the
/// server; no untrusted value is turned into a command.
/// </summary>
public sealed partial record DeployOptions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultHealthCheckTimeout = TimeSpan.FromSeconds(60);

    public required string ComposeFilePath { get; init; }

    /// <summary>Whether to run image cleanup after a successful update.</summary>
    public bool PruneImages { get; init; } = true;

    /// <summary>Maximum time the whole deploy may take before it is cancelled.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Image tag to deploy (written to the compose project's <c>.env</c> as
    /// <c>PINQOPS_TAG</c> before pulling). Null leaves the <c>.env</c> untouched,
    /// preserving pre-tag behavior.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// How long to wait for services to settle running/healthy after
    /// <c>up -d</c>. <see cref="TimeSpan.Zero"/> skips the health check.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; init; } = DefaultHealthCheckTimeout;

    /// <summary>How many recent <c>sha-*</c> images to keep locally for rollback.</summary>
    public int KeepImages { get; init; } = 5;

    /// <summary>What initiated this deploy; recorded in history (<c>ci</c>, <c>manual</c>, <c>rollback</c>).</summary>
    public string Trigger { get; init; } = DeployRecordValues.TriggerManual;

    /// <summary>
    /// Validates inputs and returns a <see cref="DeployOptions"/>. Throws when a
    /// required value is missing or invalid (fail fast).
    /// </summary>
    public static DeployOptions Create(
        string? composeFilePath,
        bool pruneImages = true,
        TimeSpan? timeout = null,
        string? tag = null,
        TimeSpan? healthCheckTimeout = null,
        int keepImages = 5,
        string trigger = DeployRecordValues.TriggerManual)
    {
        if (string.IsNullOrWhiteSpace(composeFilePath))
        {
            throw new ArgumentException("Compose file path is required.", nameof(composeFilePath));
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        // The tag lands in the .env file and in docker arguments; enforce the
        // docker tag grammar so it can never carry anything else.
        if (tag is not null && !ImageTagPattern().IsMatch(tag))
        {
            throw new ArgumentException($"'{tag}' is not a valid image tag.", nameof(tag));
        }

        var effectiveHealthTimeout = healthCheckTimeout ?? DefaultHealthCheckTimeout;
        if (effectiveHealthTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(healthCheckTimeout), "Health check timeout cannot be negative.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(keepImages, 1, nameof(keepImages));

        return new DeployOptions
        {
            ComposeFilePath = composeFilePath,
            PruneImages = pruneImages,
            Timeout = effectiveTimeout,
            Tag = tag,
            HealthCheckTimeout = effectiveHealthTimeout,
            KeepImages = keepImages,
            Trigger = trigger,
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ImageTagPattern();
}
