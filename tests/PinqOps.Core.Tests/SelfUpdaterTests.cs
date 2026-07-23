using PinqOps;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class SelfUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Update_ReplacesTheTargetWithTheDownloadedAsset()
    {
        var target = Path.Combine(_dir, "pinqops");
        File.WriteAllText(target, "old-binary");
        var downloader = new FakeFileDownloader();
        var log = new List<string>();

        var replaced = await new SelfUpdater(downloader, log.Add).UpdateAsync("pinqops", target);

        Assert.Equal(target, replaced);
        Assert.Equal("fake-archive", File.ReadAllText(target));

        // Fetched the 'latest' asset for the requested name...
        var download = Assert.Single(downloader.Downloads);
        Assert.Equal($"{SelfUpdater.ReleaseBaseUrl}/pinqops", download.Url);
        // ...beside the target (same volume, for an atomic rename)...
        Assert.Equal(_dir, Path.GetDirectoryName(download.DestinationPath));
        // ...and the temp file was renamed away, not left behind.
        Assert.False(File.Exists(download.DestinationPath));
    }

    [Fact]
    public async Task Update_MarksTheReplacedBinaryExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes are a no-op on Windows.
        }

        var target = Path.Combine(_dir, "pinqops-ui");
        File.WriteAllText(target, "old");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite); // not executable

        await new SelfUpdater(new FakeFileDownloader(), _ => { }).UpdateAsync("pinqops-ui", target);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Update_MissingDownload_KeepsTheOriginal()
    {
        var target = Path.Combine(_dir, "pinqops");
        File.WriteAllText(target, "old-binary");
        // createFile: false records the request but writes nothing to disk.
        var downloader = new FakeFileDownloader(createFile: false);
        var log = new List<string>();

        var replaced = await new SelfUpdater(downloader, log.Add).UpdateAsync("pinqops", target);

        Assert.Null(replaced);
        Assert.Equal("old-binary", File.ReadAllText(target));
        Assert.Contains(log, line => line.Contains("missing or empty"));
    }
}
