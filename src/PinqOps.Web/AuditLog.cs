using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinqOps.Web;

/// <summary>One recorded action: who did what, to which target, and whether it worked.</summary>
public sealed record AuditEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("status")] int Status);

/// <summary>
/// An append-only audit trail of every mutating dashboard action, stored as
/// JSONL so it is cheap to append and easy to tail. A single file is rotated
/// once past <see cref="MaxBytes"/> (the previous generation is kept as
/// <c>.1</c>), which bounds disk use without a background job.
/// </summary>
public sealed class AuditLog
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public AuditLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    /// <summary>Appends one entry. A logging failure must never break the request, so it is swallowed.</summary>
    public void Append(AuditEntry entry)
    {
        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, SerializerOptions) + "\n");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The audit trail is best-effort; losing a line is preferable to
            // failing the action it was recording.
        }
    }

    /// <summary>
    /// The most recent entries (newest first), optionally filtered by user or by
    /// a substring of the action. Reads both the live file and the rotated
    /// generation so a rotation does not hide recent history.
    /// </summary>
    public IReadOnlyList<AuditEntry> Read(int limit = 200, string? user = null, string? action = null)
    {
        var entries = new List<AuditEntry>();
        lock (_gate)
        {
            foreach (var file in new[] { _path, _path + ".1" })
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (JsonSerializer.Deserialize<AuditEntry>(line, SerializerOptions) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip a corrupt line rather than dropping the whole trail.
                    }
                }
            }
        }

        IEnumerable<AuditEntry> query = entries;
        if (!string.IsNullOrWhiteSpace(user))
        {
            query = query.Where(e => string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(e => e.Action.Contains(action, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToList();
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        // Keep exactly one previous generation; the older one is replaced.
        File.Move(_path, _path + ".1", overwrite: true);
    }
}
