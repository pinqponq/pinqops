using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SetupTemplatesTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("trunk")]
    public void DeployWorkflowYaml_TriggersOnTheBranchItIsCommittedTo(string defaultBranch)
    {
        // The dashboard commits this file to the repository's default branch. A
        // trigger naming any other branch never fires, and GitHub does not warn
        // about a filter that matches nothing — the pipeline would look healthy
        // and silently never deploy.
        var yaml = SetupTemplates.DeployWorkflowYaml(defaultBranch);

        Assert.Contains($"      - {defaultBranch}\n", yaml.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void DeployWorkflowYaml_IsManuallyDispatchable()
    {
        // The wizard starts the first deploy via workflow_dispatch instead of
        // making the user push a commit.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("workflow_dispatch:", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_KeepsGitHubExpressionsIntact()
    {
        // The template is an interpolated raw literal; too few '$' would swallow
        // ${{ ... }} as C# interpolation holes.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("${{ github.sha }}", yaml);
        Assert.Contains("${{ secrets.GITHUB_TOKEN }}", yaml);
        Assert.Contains("${{ steps.image.outputs.name }}", yaml);
        Assert.Contains("${GITHUB_REPOSITORY,,}", yaml);
        Assert.DoesNotContain("{{{", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_LabelsTheImageWithItsRepository()
    {
        // What connects the GHCR package to the repo; without it the deploy job
        // can push an image and then get 403 pulling it.
        Assert.Contains("org.opencontainers.image.source", SetupTemplates.DeployWorkflowYaml("main"));
    }

    [Fact]
    public void DeployWorkflowYaml_DoesNotCancelAnInFlightDeploy()
    {
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        // Builds are cancellable; a deploy killed between `down` and `up` leaves
        // the app stopped with no record.
        Assert.Contains("cancel-in-progress: false", yaml);
    }

    [Fact]
    public void ComposeYaml_QuotesTheProjectNameAndPublishesThePort()
    {
        var yaml = SetupTemplates.ComposeYaml("Acme", "2048", 8080, 80);

        // Unquoted, an all-digit name is a YAML integer and compose rejects the file.
        Assert.Contains("name: \"2048\"", yaml);
        Assert.Contains("\"${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-80}\"", yaml);
        Assert.Contains("ghcr.io/acme/2048", yaml);
    }
}
