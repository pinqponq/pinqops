namespace PinqOps.Tests.Fakes;

/// <summary>Records download requests and optionally writes a placeholder file.</summary>
public sealed class FakeFileDownloader : IFileDownloader
{
    private readonly bool _createFile;

    public FakeFileDownloader(bool createFile = true)
    {
        _createFile = createFile;
    }

    public List<(string Url, string DestinationPath)> Downloads { get; } = new();

    public Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        Downloads.Add((url, destinationPath));
        if (_createFile)
        {
            File.WriteAllText(destinationPath, "fake-archive");
        }

        return Task.CompletedTask;
    }
}
