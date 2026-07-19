using Xunit;

namespace PinqOps.Web.Tests;

public class AppCredentialStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly AppCredentialStore _store;

    public AppCredentialStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-creds-tests").FullName;
        _store = new AppCredentialStore(Path.Combine(_directory, "app-credentials.json"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void GetOrCreatePassword_IsStableAcrossCalls()
    {
        var first = _store.GetOrCreatePassword("postgres");
        var second = _store.GetOrCreatePassword("postgres");

        Assert.Equal(first, second);
        Assert.Equal(PasswordGenerator.Length, first.Length);
    }

    [Fact]
    public void GetOrCreatePassword_DiffersPerApp_AndIsCaseInsensitive()
    {
        var postgres = _store.GetOrCreatePassword("postgres");
        var mysql = _store.GetOrCreatePassword("mysql");

        Assert.NotEqual(postgres, mysql);
        Assert.Equal(postgres, _store.GetOrCreatePassword("Postgres"));
    }

    [Fact]
    public void SetEnv_PersistsRetrievableCredentials_WithOwnerOnlyPermissions()
    {
        _store.SetEnv("postgres", new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "s3cret" });

        var reloaded = new AppCredentialStore(_store.Path_);
        Assert.Equal("s3cret", reloaded.Get("postgres")!["POSTGRES_PASSWORD"]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.Path_));
        }
    }

    [Fact]
    public void Get_UnknownApp_ReturnsNull()
    {
        Assert.Null(_store.Get("nope"));
    }

    [Fact]
    public void CorruptFile_StartsFresh()
    {
        File.WriteAllText(_store.Path_, "{broken");

        Assert.Null(_store.Get("postgres"));
        Assert.NotNull(_store.GetOrCreatePassword("postgres"));
    }
}
