using Xunit;

namespace PinqOps.Web.Tests;

public class AppCatalogTests
{
    [Fact]
    public void NoCatalogEntryShipsAHardcodedPassword()
    {
        // Guard: every credential-bearing env entry must use a {{password}}
        // token — literal "pinqops"-style defaults must never come back.
        var offenders = AppCatalog.Apps
            .SelectMany(app => app.Env.Select(env => (app.Id, Env: env)))
            .Where(pair =>
                (pair.Env.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                 || pair.Env.Contains("_KEY", StringComparison.OrdinalIgnoreCase)
                 || pair.Env.Contains("AUTH=", StringComparison.OrdinalIgnoreCase))
                && !pair.Env.Contains("{{password", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ResolveEnv_SubstitutesOwnPassword_AndReportsCredential()
    {
        var spec = AppCatalog.Find("postgres")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, appId =>
        {
            Assert.Equal("postgres", appId);
            return "s3cret";
        });

        Assert.Contains("POSTGRES_PASSWORD=s3cret", env);
        Assert.Equal("s3cret", credentials["POSTGRES_PASSWORD"]);
    }

    [Fact]
    public void ResolveEnv_CompositeValue_KeepsSurroundingText()
    {
        var spec = AppCatalog.Find("neo4j")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, _ => "s3cret");

        Assert.Contains("NEO4J_AUTH=neo4j/s3cret", env);
        Assert.Equal("neo4j/s3cret", credentials["NEO4J_AUTH"]);
    }

    [Fact]
    public void ResolveEnv_CrossAppToken_UsesReferencedAppsPassword()
    {
        var spec = AppCatalog.Find("wordpress")!;
        var asked = new List<string>();

        var (env, _) = AppCatalog.ResolveEnv(spec, appId => { asked.Add(appId); return "mysql-pw"; });

        Assert.Contains("WORDPRESS_DB_PASSWORD=mysql-pw", env);
        Assert.Contains("mysql", asked);
    }

    [Fact]
    public void ResolveEnv_NoTokens_PassesThroughUntouched()
    {
        var spec = AppCatalog.Find("elasticsearch")!;

        var (env, credentials) = AppCatalog.ResolveEnv(spec, _ => throw new InvalidOperationException("must not be called"));

        Assert.Equal(spec.Env, env);
        Assert.Empty(credentials);
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generate_ProducesDistinctAlphanumericValuesOfFixedLength()
    {
        var first = PasswordGenerator.Generate();
        var second = PasswordGenerator.Generate();

        Assert.Equal(PasswordGenerator.Length, first.Length);
        Assert.True(first.All(char.IsAsciiLetterOrDigit));
        Assert.NotEqual(first, second);
    }
}
