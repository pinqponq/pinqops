using Xunit;

namespace PinqOps.Tests;

public class DockerComposeCommandBuilderTests
{
    private const string ComposePath = "/opt/pinqops/docker-compose.yml";

    [Fact]
    public void Pull_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "pull" },
            DockerComposeCommandBuilder.Pull(ComposePath));
    }

    [Fact]
    public void Up_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "up", "-d" },
            DockerComposeCommandBuilder.Up(ComposePath));
    }

    [Fact]
    public void PruneImages_BuildsFixedArguments()
    {
        Assert.Equal(new[] { "image", "prune", "-f" }, DockerComposeCommandBuilder.PruneImages());
    }

    [Fact]
    public void Ps_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "ps", "-a", "--format", "json" },
            DockerComposeCommandBuilder.Ps(ComposePath));
    }

    [Fact]
    public void ConfigImages_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "compose", "-f", ComposePath, "config", "--images" },
            DockerComposeCommandBuilder.ConfigImages(ComposePath));
    }

    [Fact]
    public void ListRepoImages_BuildsFixedArguments()
    {
        Assert.Equal(
            new[] { "images", "ghcr.io/o/r", "--format", "{{json .}}" },
            DockerComposeCommandBuilder.ListRepoImages("ghcr.io/o/r"));
    }

    [Fact]
    public void RemoveImage_And_InspectImage_KeepReferenceAsSingleArgument()
    {
        Assert.Equal(new[] { "rmi", "ghcr.io/o/r:sha-1" }, DockerComposeCommandBuilder.RemoveImage("ghcr.io/o/r:sha-1"));
        Assert.Equal(new[] { "image", "inspect", "ghcr.io/o/r:sha-1" }, DockerComposeCommandBuilder.InspectImage("ghcr.io/o/r:sha-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Pull_RejectsBlankPath(string? path)
    {
        // null → ArgumentNullException, blank → ArgumentException; both derive
        // from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => DockerComposeCommandBuilder.Pull(path!));
    }

    [Fact]
    public void Up_KeepsPathAsSingleArgument_NoInjection()
    {
        // A hostile-looking path must remain ONE argument; it is never split or
        // interpreted as extra flags/commands.
        const string trickyPath = "/opt/pinqops/docker-compose.yml; rm -rf /";
        var arguments = DockerComposeCommandBuilder.Up(trickyPath);

        Assert.Equal(trickyPath, arguments[2]);
        Assert.Equal(5, arguments.Count);
    }
}
