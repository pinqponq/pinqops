namespace PinqOps;

/// <summary>
/// Builds the fixed <c>docker</c> argument lists used by a deployment. Every
/// argument is a discrete list item, so a compose file path can never inject an
/// extra command or flag.
/// </summary>
public static class DockerComposeCommandBuilder
{
    /// <summary><c>docker compose -f &lt;path&gt; pull</c></summary>
    public static IReadOnlyList<string> Pull(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "pull" };
    }

    /// <summary><c>docker compose -f &lt;path&gt; up -d</c></summary>
    public static IReadOnlyList<string> Up(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "up", "-d" };
    }

    /// <summary><c>docker image prune -f</c></summary>
    public static IReadOnlyList<string> PruneImages() => new[] { "image", "prune", "-f" };
}
