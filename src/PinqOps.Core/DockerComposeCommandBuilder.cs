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

    /// <summary><c>docker compose -f &lt;path&gt; ps -a --format json</c></summary>
    public static IReadOnlyList<string> Ps(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "ps", "-a", "--format", "json" };
    }

    /// <summary><c>docker compose -f &lt;path&gt; config --images</c></summary>
    public static IReadOnlyList<string> ConfigImages(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "config", "--images" };
    }

    /// <summary><c>docker images &lt;repo&gt; --format {{json .}}</c></summary>
    public static IReadOnlyList<string> ListRepoImages(string imageRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRepository);
        return new[] { "images", imageRepository, "--format", "{{json .}}" };
    }

    /// <summary><c>docker rmi &lt;reference&gt;</c></summary>
    public static IReadOnlyList<string> RemoveImage(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new[] { "rmi", reference };
    }

    /// <summary><c>docker image inspect &lt;reference&gt;</c></summary>
    public static IReadOnlyList<string> InspectImage(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new[] { "image", "inspect", reference };
    }

    /// <summary><c>docker image prune -f</c></summary>
    public static IReadOnlyList<string> PruneImages() => new[] { "image", "prune", "-f" };
}
