using Xunit;

namespace PinqOps.Tests;

public class DeployHistoryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _composePath;
    private readonly DeployHistoryStore _store;

    public DeployHistoryStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-history-tests").FullName;
        _composePath = Path.Combine(_directory, "docker-compose.yml");
        _store = new DeployHistoryStore(_composePath);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static DeployRecord Record(string tag, string result = DeployRecordValues.ResultSucceeded) => new()
    {
        Id = DeployHistoryStore.NewRecordId(),
        Tag = tag,
        StartedAt = DateTimeOffset.UtcNow,
        Result = result,
        Trigger = DeployRecordValues.TriggerCi,
    };

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void Append_StoresNewestFirst_InStateDirectory()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2"));

        var records = _store.Load();
        Assert.Equal(2, records.Count);
        Assert.Equal("sha-2", records[0].Tag);
        Assert.Equal("sha-1", records[1].Tag);
        Assert.True(File.Exists(Path.Combine(_directory, ".pinqops", "history.json")));
    }

    [Fact]
    public void Append_CapsAtMaxEntries()
    {
        for (var i = 0; i < DeployHistoryStore.MaxEntries + 5; i++)
        {
            _store.Append(Record($"sha-{i}"));
        }

        var records = _store.Load();
        Assert.Equal(DeployHistoryStore.MaxEntries, records.Count);
        Assert.Equal($"sha-{DeployHistoryStore.MaxEntries + 4}", records[0].Tag);
    }

    [Fact]
    public void LastSuccessfulTagBefore_SkipsFailuresAndCurrentTag()
    {
        _store.Append(Record("sha-1"));
        _store.Append(Record("sha-2", DeployRecordValues.ResultFailed));
        _store.Append(Record("sha-3"));

        Assert.Equal("sha-1", _store.LastSuccessfulTagBefore("sha-3"));
        Assert.Equal("sha-3", _store.LastSuccessfulTagBefore("sha-9"));

        // History is keyed by project DIRECTORY; an empty project has none.
        var emptyDirectory = Directory.CreateDirectory(Path.Combine(_directory, "empty")).FullName;
        Assert.Null(new DeployHistoryStore(Path.Combine(emptyDirectory, "docker-compose.yml")).LastSuccessfulTagBefore("x"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".pinqops"));
        File.WriteAllText(Path.Combine(_directory, ".pinqops", "history.json"), "{not json");

        Assert.Empty(_store.Load());
    }
}
