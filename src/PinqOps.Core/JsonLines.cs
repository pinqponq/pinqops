using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Docker's <c>--format json</c> output is NDJSON in some versions and a
/// single array in others; accept both.
/// </summary>
public static class JsonLines
{
    public static List<JsonElement> Parse(string output)
    {
        var items = new List<JsonElement>();
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return items;
        }

        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            items.AddRange(document.RootElement.EnumerateArray().Select(element => element.Clone()));
            return items;
        }

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            items.Add(document.RootElement.Clone());
        }

        return items;
    }
}
