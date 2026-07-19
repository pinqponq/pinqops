using System.Security.Cryptography;
using System.Text.Json;

namespace PinqOps;

/// <summary>
/// Persists deploy history as JSON in the <c>.pinqops</c> state directory next
/// to the compose file (newest first, capped). A corrupt file is treated as
/// empty history rather than failing a deploy.
/// </summary>
public sealed class DeployHistoryStore
{
    public const int MaxEntries = 100;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public DeployHistoryStore(string composeFilePath)
    {
        _path = PinqOpsStatePaths.HistoryFile(composeFilePath);
    }

    public string Path_ => _path;

    /// <summary>Returns all records, newest first.</summary>
    public IReadOnlyList<DeployRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var document = JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(_path), SerializerOptions);
                return document?.Deployments ?? new List<DeployRecord>();
            }
        }
        catch (JsonException)
        {
            // A corrupt history file must not brick deploys; start fresh.
        }

        return Array.Empty<DeployRecord>();
    }

    /// <summary>Prepends a record and persists, trimming to <see cref="MaxEntries"/>.</summary>
    public void Append(DeployRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var records = new List<DeployRecord> { record };
        records.AddRange(Load());
        if (records.Count > MaxEntries)
        {
            records.RemoveRange(MaxEntries, records.Count - MaxEntries);
        }

        Save(records);
    }

    /// <summary>
    /// The most recent successfully deployed tag different from
    /// <paramref name="currentTag"/> — the default rollback target.
    /// </summary>
    public string? LastSuccessfulTagBefore(string? currentTag)
    {
        foreach (var record in Load())
        {
            if (record.Result == DeployRecordValues.ResultSucceeded && record.Tag != currentTag)
            {
                return record.Tag;
            }
        }

        return null;
    }

    /// <summary>The most recent successful record, when any.</summary>
    public DeployRecord? LastSuccessful() =>
        Load().FirstOrDefault(record => record.Result == DeployRecordValues.ResultSucceeded);

    public static string NewRecordId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private void Save(List<DeployRecord> records)
    {
        // Atomic write so a crash mid-save cannot truncate the history file.
        SecureFile.WriteAllText(
            _path,
            JsonSerializer.Serialize(new HistoryDocument { Deployments = records }, SerializerOptions));
    }

    private sealed class HistoryDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<DeployRecord> Deployments { get; set; } = new();
    }
}
