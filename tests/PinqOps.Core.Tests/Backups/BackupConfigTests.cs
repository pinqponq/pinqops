using PinqOps.Backups;
using Xunit;

namespace PinqOps.Tests.Backups;

public class BackupScheduleTests
{
    private static BackupTarget Target(string schedule, int atHour = 3) =>
        new() { Id = "t", Schedule = schedule, AtHour = atHour, Enabled = true };

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Hourly_DueWhenNeverRun_AndAfterAnHour()
    {
        var target = Target("hourly");
        Assert.True(BackupSchedule.IsDue(target, At(1, 10), lastRun: null));
        Assert.False(BackupSchedule.IsDue(target, At(1, 10, 30), lastRun: At(1, 10)));
        Assert.True(BackupSchedule.IsDue(target, At(1, 11), lastRun: At(1, 10)));
    }

    [Fact]
    public void Daily_DueOnlyAtItsHour_OncePerDay()
    {
        var target = Target("daily", atHour: 3);
        Assert.False(BackupSchedule.IsDue(target, At(1, 2), lastRun: null)); // wrong hour
        Assert.True(BackupSchedule.IsDue(target, At(1, 3), lastRun: null));  // right hour, never run
        Assert.False(BackupSchedule.IsDue(target, At(1, 3, 30), lastRun: At(1, 3))); // already ran this window
        Assert.True(BackupSchedule.IsDue(target, At(2, 3), lastRun: At(1, 3))); // next day
    }

    [Fact]
    public void Weekly_DueOnMondayAtItsHour()
    {
        var target = Target("weekly", atHour: 4);
        // 2026-07-06 is a Monday.
        Assert.True(BackupSchedule.IsDue(target, At(6, 4), lastRun: null));
        Assert.False(BackupSchedule.IsDue(target, At(7, 4), lastRun: null)); // Tuesday
        Assert.False(BackupSchedule.IsDue(target, At(6, 4, 10), lastRun: At(6, 4)));
    }
}

public class BackupNamingTests
{
    [Theory]
    [InlineData("postgres", "sql")]
    [InlineData("mysql", "sql")]
    [InlineData("mongo", "archive")]
    [InlineData("redis", "rdb")]
    [InlineData("volume", "tgz")]
    public void Extension_MatchesTheEngine(string engine, string ext)
    {
        Assert.Equal(ext, BackupNaming.Extension(engine));
    }

    [Fact]
    public void FileName_IsTimestampedAndValid()
    {
        var name = BackupNaming.FileName("postgres", new DateTimeOffset(2026, 7, 22, 3, 4, 5, TimeSpan.Zero));

        Assert.Equal("20260722-030405.sql", name);
        Assert.True(BackupNaming.IsValidSnapshot(name));
    }

    [Theory]
    [InlineData("20260722-030405.sql", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("20260722-030405.exe", false)]
    [InlineData("dump.sql", false)]
    public void IsValidSnapshot_RejectsUnsafeNames(string name, bool valid)
    {
        Assert.Equal(valid, BackupNaming.IsValidSnapshot(name));
    }

    [Theory]
    [InlineData("db-postgres", true)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    public void IsValidId_RejectsPathSeparators(string id, bool valid)
    {
        Assert.Equal(valid, BackupNaming.IsValidId(id));
    }
}

public class BackupConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-backups-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "backups.json");
        var store = new BackupConfigStore(path);
        store.Save(new BackupConfig
        {
            Targets = [new BackupTarget { Id = "db-postgres", Kind = "db", Name = "pinqops-postgres", Engine = "postgres", Schedule = "daily", AtHour = 3, RetentionCount = 5 }],
        });

        var loaded = new BackupConfigStore(path).Load();

        var target = Assert.Single(loaded.Targets);
        Assert.Equal("db-postgres", target.Id);
        Assert.Equal("postgres", target.Engine);
        Assert.Equal(5, target.RetentionCount);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var path = Path.Combine(_dir, "backups.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new BackupConfigStore(path).Load().Targets);
    }
}
