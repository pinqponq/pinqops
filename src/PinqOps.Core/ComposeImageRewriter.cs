using System.Text.RegularExpressions;

namespace PinqOps;

/// <summary>
/// Converts the application service's hardcoded <c>image:</c> line to the
/// env-driven form (<c>${PINQOPS_IMAGE:-…}:${PINQOPS_TAG:-latest}</c>) so the
/// deployed image follows the repository even after a rename. It rewrites only
/// the first <c>image:</c> line that looks pinqops-generated (a <c>ghcr.io</c>
/// reference or one already using <c>${PINQOPS_TAG}</c>), preserving indentation
/// and every other line — so a user's ports, volumes, env, and any additional
/// services are left untouched.
/// </summary>
public static partial class ComposeImageRewriter
{
    /// <summary>
    /// Returns <paramref name="composeText"/> with the app image line converted
    /// to the env-driven form, or the text unchanged when it is already
    /// env-driven or has no pinqops-style image line to convert.
    /// </summary>
    public static string ToEnvDriven(string composeText, string defaultImage)
    {
        ArgumentNullException.ThrowIfNull(composeText);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultImage);

        var lines = composeText.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = ImageLinePattern().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (value.StartsWith($"${{{Deployer.ImageVariable}", StringComparison.Ordinal))
            {
                // Already env-driven; nothing to do.
                return composeText;
            }

            if (!IsPinqopsStyleImage(value))
            {
                // Belongs to some other service; leave it and keep scanning.
                continue;
            }

            var indent = match.Groups["indent"].Value;
            lines[index] = $"{indent}image: ${{{Deployer.ImageVariable}:-{defaultImage}}}:${{{Deployer.TagVariable}:-latest}}";
            return string.Join('\n', lines);
        }

        return composeText;
    }

    private static bool IsPinqopsStyleImage(string value) =>
        value.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase)
        || value.Contains($"${{{Deployer.TagVariable}", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<indent>\s*)image:\s*(?<value>\S.*?)\s*$")]
    private static partial Regex ImageLinePattern();
}
