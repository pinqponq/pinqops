using Xunit;

namespace PinqOps.Tests;

public class DeployOptionsTests
{
    [Fact]
    public void Create_AppliesDefaults()
    {
        var options = DeployOptions.Create("/opt/pinqops/docker-compose.yml");

        Assert.Equal("/opt/pinqops/docker-compose.yml", options.ComposeFilePath);
        Assert.True(options.PruneImages);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_RejectsBlankComposePath(string? path)
    {
        Assert.Throws<ArgumentException>(() => DeployOptions.Create(path));
    }

    [Fact]
    public void Create_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DeployOptions.Create("/opt/pinqops/docker-compose.yml", timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Create_HonorsExplicitValues()
    {
        var options = DeployOptions.Create(
            "/srv/app/docker-compose.yml",
            pruneImages: false,
            timeout: TimeSpan.FromSeconds(90));

        Assert.False(options.PruneImages);
        Assert.Equal(TimeSpan.FromSeconds(90), options.Timeout);
    }
}
