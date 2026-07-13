namespace PinqOps;

/// <summary>
/// Options for a single deployment. The compose file path is fixed on the
/// server; no untrusted value is turned into a command.
/// </summary>
public sealed record DeployOptions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public required string ComposeFilePath { get; init; }

    /// <summary>Whether to run <c>docker image prune -f</c> after a successful update.</summary>
    public bool PruneImages { get; init; } = true;

    /// <summary>Maximum time the whole deploy may take before it is cancelled.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Validates inputs and returns a <see cref="DeployOptions"/>. Throws when a
    /// required value is missing or invalid (fail fast).
    /// </summary>
    public static DeployOptions Create(string? composeFilePath, bool pruneImages = true, TimeSpan? timeout = null)
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

        return new DeployOptions
        {
            ComposeFilePath = composeFilePath,
            PruneImages = pruneImages,
            Timeout = effectiveTimeout,
        };
    }
}
