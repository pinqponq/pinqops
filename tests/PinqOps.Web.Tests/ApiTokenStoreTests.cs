using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ApiTokenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-tokens-").FullName;

    private ApiTokenStore Store() => new(Path.Combine(_dir, "tokens.json"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTimeOffset Now = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ReturnsAPrefixedPlaintextValidatedOnce()
    {
        var store = Store();

        var (token, plaintext) = store.Create("ci", "deploy", Now);

        Assert.StartsWith("pot_", plaintext);
        Assert.EndsWith(token.Last4, plaintext);
        Assert.Equal("deploy", store.Validate(plaintext, Now));
    }

    [Fact]
    public void Validate_UnknownToken_IsNull()
    {
        Assert.Null(Store().Validate("pot_nope", Now));
    }

    [Fact]
    public void PlaintextIsNotRecoverableFromStorage()
    {
        var store = Store();
        var (_, plaintext) = store.Create("t", "read", Now);

        var stored = store.List().Single();

        Assert.DoesNotContain(plaintext, stored.Sha256);
        Assert.NotEqual(plaintext, stored.Sha256);
    }

    [Fact]
    public void Delete_RemovesTheToken()
    {
        var store = Store();
        var (token, plaintext) = store.Create("t", "admin", Now);

        Assert.True(store.Delete(token.Id));
        Assert.Null(store.Validate(plaintext, Now));
    }

    [Fact]
    public void Create_InvalidScope_FallsBackToRead()
    {
        var (token, _) = Store().Create("t", "superuser", Now);
        Assert.Equal("read", token.Scope);
    }
}

public class ApiScopesTests
{
    [Theory]
    [InlineData("GET", "/api/backups", "read")]
    [InlineData("POST", "/api/deploy/rollback", "deploy")]
    [InlineData("POST", "/api/setup/trigger-deploy", "deploy")]
    [InlineData("POST", "/api/compose/apply", "deploy")]
    [InlineData("POST", "/api/apps/install", "deploy")]
    [InlineData("POST", "/api/backups/run/db-postgres", "deploy")]
    [InlineData("POST", "/api/settings", "admin")]
    [InlineData("POST", "/api/tokens", "admin")]
    [InlineData("POST", "/api/domains", "admin")]
    [InlineData("POST", "/api/backups/targets", "admin")]
    [InlineData("DELETE", "/api/tokens/abc", "admin")]
    public void RequiredFor_MapsRoutesToScopes(string method, string path, string expected)
    {
        Assert.Equal(expected, ApiScopes.RequiredFor(method, path));
    }

    [Theory]
    [InlineData("read", "read", true)]
    [InlineData("deploy", "read", true)]
    [InlineData("admin", "deploy", true)]
    [InlineData("read", "deploy", false)]
    [InlineData("deploy", "admin", false)]
    public void Satisfies_RespectsTheHierarchy(string have, string need, bool ok)
    {
        Assert.Equal(ok, ApiScopes.Satisfies(have, need));
    }
}
