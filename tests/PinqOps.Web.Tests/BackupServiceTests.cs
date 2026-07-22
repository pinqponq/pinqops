using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class BackupServiceTests
{
    [Fact]
    public void DumpPlan_Postgres_UsesPgDumpallToATempFile()
    {
        var (command, file) = BackupService.DumpPlan("postgres");

        Assert.Equal(["pg_dumpall", "-U", "postgres", "-f", "/tmp/pinqops-backup.sql"], command);
        Assert.Equal("/tmp/pinqops-backup.sql", file);
    }

    [Fact]
    public void DumpPlan_Mysql_ReadsThePasswordFromTheContainerEnv()
    {
        var (command, _) = BackupService.DumpPlan("mysql");

        // The password is expanded inside the container, never on our argv.
        Assert.Equal("sh", command[0]);
        Assert.Contains("$MYSQL_ROOT_PASSWORD", command[2]);
        Assert.Contains("mysqldump", command[2]);
    }

    [Fact]
    public void DumpPlan_Redis_TargetsTheLiveRdb()
    {
        var (command, file) = BackupService.DumpPlan("redis");

        Assert.Equal(["redis-cli", "SAVE"], command);
        Assert.Equal("/data/dump.rdb", file);
    }

    [Fact]
    public void DumpPlan_UnknownEngine_Throws()
    {
        Assert.Throws<ArgumentException>(() => BackupService.DumpPlan("cassandra"));
    }

    [Fact]
    public void RestorePlan_Mongo_Drops()
    {
        Assert.Equal(["mongorestore", "--archive=/tmp/pinqops-restore.archive", "--drop"], BackupService.RestorePlan("mongo"));
    }
}
