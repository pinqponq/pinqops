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
