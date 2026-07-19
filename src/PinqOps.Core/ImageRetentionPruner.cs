using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Keeps a bounded set of app images on disk instead of blanket-pruning:
/// <c>latest</c> plus the newest N <c>sha-*</c> tags survive, older SHA tags
/// are removed. Keeping recent tagged images locally is what makes rollback
/// work without registry credentials. Dangling layers are still pruned.
/// </summary>
public sealed class ImageRetentionPruner
{
    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;

    public ImageRetentionPruner(IProcessRunner processRunner, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
    }

    /// <summary>
    /// Best effort: failures are logged but never fail the deploy. Applies
    /// retention to every image repository the compose project references.
    /// </summary>
    public async Task PruneAsync(string composeFilePath, int keepImages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepImages, 1);

        var imagesResult = await RunAsync(DockerComposeCommandBuilder.ConfigImages(composeFilePath), cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            _log?.Invoke($"image retention skipped: compose config --images failed: {imagesResult.StandardError.TrimEnd()}");
            return;
        }

        var repositories = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RepositoryOf)
            .Where(repo => repo.Length > 0)
            .Distinct()
            .ToList();

        foreach (var repository in repositories)
        {
            await PruneRepositoryAsync(repository, keepImages, cancellationToken).ConfigureAwait(false);
        }

        // Dangling layers left behind by removed tags.
        await RunAsync(DockerComposeCommandBuilder.PruneImages(), cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneRepositoryAsync(string repository, int keepImages, CancellationToken cancellationToken)
    {
        var listResult = await RunAsync(DockerComposeCommandBuilder.ListRepoImages(repository), cancellationToken)
            .ConfigureAwait(false);
        if (!listResult.Succeeded)
        {
            _log?.Invoke($"image retention skipped for {repository}: {listResult.StandardError.TrimEnd()}");
            return;
        }

        // `docker images` lists newest first; keep that order for retention.
        var shaTags = new List<string>();
        foreach (var element in JsonLines.Parse(listResult.StandardOutput))
        {
            if (element.TryGetProperty("Tag", out var tagProperty)
                && tagProperty.ValueKind == JsonValueKind.String
                && tagProperty.GetString() is { } tag
                && tag.StartsWith("sha-", StringComparison.Ordinal)
                && !shaTags.Contains(tag))
            {
                shaTags.Add(tag);
            }
        }

        foreach (var tag in shaTags.Skip(keepImages))
        {
            var reference = $"{repository}:{tag}";
            var removeResult = await RunAsync(DockerComposeCommandBuilder.RemoveImage(reference), cancellationToken)
                .ConfigureAwait(false);
            _log?.Invoke(removeResult.Succeeded
                ? $"removed old image {reference}"
                : $"could not remove {reference}: {removeResult.StandardError.TrimEnd()}");
        }
    }

    /// <summary>Strips a trailing <c>:tag</c> (but not a registry port) from an image reference.</summary>
    public static string RepositoryOf(string imageReference)
    {
        var lastColon = imageReference.LastIndexOf(':');
        if (lastColon < 0)
        {
            return imageReference;
        }

        // A colon after the last slash separates the tag; before it, a port.
        var lastSlash = imageReference.LastIndexOf('/');
        return lastColon > lastSlash ? imageReference[..lastColon] : imageReference;
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _processRunner.RunAsync("docker", arguments, workingDirectory: null, cancellationToken);
}
