using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class AuditLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public AuditLogTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-audit-tests").FullName;
        _path = Path.Combine(_directory, "audit.jsonl");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AuditEntry Entry(string user, string action, string result = "ok", int status = 200) =>
        new(DateTimeOffset.UnixEpoch, user, action, string.Empty, result, status);

    [Fact]
    public void Read_ReturnsAppendedEntriesNewestFirst()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/deploy/rollback") with { Timestamp = DateTimeOffset.UnixEpoch });
        log.Append(Entry("bob", "DELETE /api/users/x") with { Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(1) });

        var items = log.Read();

        Assert.Equal(2, items.Count);
        Assert.Equal("bob", items[0].User);
        Assert.Equal("alice", items[1].User);
    }

    [Fact]
    public void Read_FiltersByUserAndAction()
    {
        var log = new AuditLog(_path);
        log.Append(Entry("alice", "POST /api/deploy/rollback"));
        log.Append(Entry("bob", "POST /api/users"));
        log.Append(Entry("alice", "POST /api/users"));

        Assert.Equal(2, log.Read(user: "alice").Count);
        Assert.All(log.Read(action: "/api/users"), e => Assert.Contains("/api/users", e.Action));
        Assert.Single(log.Read(user: "alice", action: "rollback"));
    }

    [Fact]
    public void Read_ToleratesACorruptLine()
    {
        File.WriteAllText(_path, "{ not json\n" + """{"ts":"1970-01-01T00:00:00+00:00","user":"a","action":"x","target":"","result":"ok","status":200}""" + "\n");

        var items = new AuditLog(_path).Read();

        Assert.Single(items);
        Assert.Equal("a", items[0].User);
    }

    [Fact]
    public void Append_MissingDirectoryIsCreated()
    {
        var nested = Path.Combine(_directory, "sub", "dir", "audit.jsonl");
        var log = new AuditLog(nested);

        log.Append(Entry("alice", "POST /api/x"));

        Assert.True(File.Exists(nested));
        Assert.Single(log.Read());
    }
}
