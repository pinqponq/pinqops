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

        // `docker images` lists newest first, but that ordering is not
        // guaranteed and is by image *Created* time — a re-pulled or
        // out-of-order-built tag can sort unexpectedly. Read CreatedAt and sort
        // explicitly so retention never deletes the newest image (the one a
        // rollback needs) by trusting the listing order.
        var shaTags = new List<(string Tag, DateTimeOffset? Created)>();
        foreach (var element in JsonLines.Parse(listResult.StandardOutput))
        {
            if (element.TryGetProperty("Tag", out var tagProperty)
                && tagProperty.ValueKind == JsonValueKind.String
                && tagProperty.GetString() is { } tag
                && tag.StartsWith("sha-", StringComparison.Ordinal)
                && !shaTags.Any(existing => existing.Tag == tag))
            {
                var created = element.TryGetProperty("CreatedAt", out var createdProperty)
                    && createdProperty.ValueKind == JsonValueKind.String
                        ? ParseDockerCreatedAt(createdProperty.GetString())
                        : null;
                shaTags.Add((tag, created));
            }
        }

        // Only reorder when every tag carries a parseable timestamp; otherwise
        // fall back to docker's newest-first listing order.
        var ordered = shaTags.All(entry => entry.Created is not null)
            ? shaTags.OrderByDescending(entry => entry.Created!.Value).Select(entry => entry.Tag)
            : shaTags.Select(entry => entry.Tag);

        foreach (var tag in ordered.Skip(keepImages))
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

    /// <summary>Parses docker's <c>CreatedAt</c> ("2024-06-13 08:15:30 +0000 UTC").</summary>
    private static DateTimeOffset? ParseDockerCreatedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        // Drop the trailing timezone name so the numeric offset can be parsed.
        if (value.EndsWith(" UTC", StringComparison.Ordinal))
        {
            value = value[..^4].TrimEnd();
        }

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _processRunner.RunAsync("docker", arguments, workingDirectory: null, cancellationToken);
}
