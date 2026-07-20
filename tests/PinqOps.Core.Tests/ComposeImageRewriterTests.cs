using Xunit;

namespace PinqOps.Tests;

public class ComposeImageRewriterTests
{
    [Fact]
    public void ToEnvDriven_ConvertsHardcodedImage_PreservingOtherLines()
    {
        var compose =
            """
            name: pinqops
            services:
              app:
                image: ghcr.io/acme/old-name:${PINQOPS_TAG:-latest}
                restart: unless-stopped
                ports:
                  - "8080:80"
            """;

        var result = ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/new-name");

        Assert.Contains("image: ${PINQOPS_IMAGE:-ghcr.io/acme/new-name}:${PINQOPS_TAG:-latest}", result);
        Assert.DoesNotContain("old-name", result);
        // Everything else is untouched.
        Assert.Contains("restart: unless-stopped", result);
        Assert.Contains("\"8080:80\"", result);
    }

    [Fact]
    public void ToEnvDriven_KeepsIndentation()
    {
        var compose = "services:\n  app:\n    image: ghcr.io/acme/app:latest\n";

        var result = ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/app");

        Assert.Contains("\n    image: ${PINQOPS_IMAGE:-ghcr.io/acme/app}:${PINQOPS_TAG:-latest}", result);
    }

    [Fact]
    public void ToEnvDriven_AlreadyEnvDriven_ReturnsUnchanged()
    {
        var compose = "    image: ${PINQOPS_IMAGE:-ghcr.io/acme/app}:${PINQOPS_TAG:-latest}\n";

        Assert.Equal(compose, ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/app"));
    }

    [Fact]
    public void ToEnvDriven_NonPinqopsImage_ReturnsUnchanged()
    {
        // A user's own service image (not ghcr.io, no PINQOPS_TAG) must be left alone.
        var compose = "    image: redis:7\n";

        Assert.Equal(compose, ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/app"));
    }

    [Fact]
    public void ToEnvDriven_NoImageLine_ReturnsUnchanged()
    {
        var compose = "services:\n  app:\n    build: .\n";

        Assert.Equal(compose, ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/app"));
    }

    [Fact]
    public void ToEnvDriven_ConvertsOnlyTheFirstPinqopsImage_SkippingOtherServices()
    {
        var compose =
            """
            services:
              redis:
                image: redis:7
              app:
                image: ghcr.io/acme/app:${PINQOPS_TAG:-latest}
            """;

        var result = ComposeImageRewriter.ToEnvDriven(compose, "ghcr.io/acme/app");

        Assert.Contains("image: redis:7", result);
        Assert.Contains("image: ${PINQOPS_IMAGE:-ghcr.io/acme/app}:${PINQOPS_TAG:-latest}", result);
    }
}
