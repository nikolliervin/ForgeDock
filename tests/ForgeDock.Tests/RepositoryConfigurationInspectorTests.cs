using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class RepositoryConfigurationInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public RepositoryConfigurationInspectorTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void DetectsWrongServiceAndHostPortWithoutGuessingBetweenServices()
    {
        const string json =
            """{"services":{"vote":{"ports":["8080:80"]},"result":{"ports":[{"target":80,"published":"8081"}]},"redis":{"expose":["6379"]}}}""";
        var wrong = RepositoryConfigurationInspector.InspectCompose(
            root,
            json,
            ["compose.yaml"],
            "compose.yaml",
            "gateway",
            8080
        );
        Assert.Contains(
            wrong.Issues,
            i => i.Severity == "error" && i.Message.Contains("does not exist")
        );
        Assert.Equal([80], wrong.Services.Single(s => s.Name == "vote").Ports);
        var port = RepositoryConfigurationInspector.InspectCompose(
            root,
            json,
            [],
            "compose.yaml",
            "vote",
            8080
        );
        Assert.Contains(port.Issues, i => i.Message.Contains("host ports"));
        var valid = RepositoryConfigurationInspector.InspectCompose(
            root,
            json,
            [],
            "compose.yaml",
            "vote",
            80
        );
        Assert.DoesNotContain(valid.Issues, i => i.Severity == "error");
    }

    [Fact]
    public void FindsMissingCopyInputsAndSkipsStageCopies()
    {
        File.WriteAllText(
            Path.Combine(root, "Dockerfile"),
            "COPY [\"local-nuget-packages/\", \"/packages/\"]\nCOPY --from=build /app /app\n"
        );
        var check = RepositoryConfigurationInspector.InspectCompose(
            root,
            """{"services":{"api":{"build":{"context":".","dockerfile":"Dockerfile"},"expose":[80]}}}""",
            [],
            "compose.yaml",
            "api",
            80
        );
        Assert.Single(check.Issues, i => i.Message.Contains("local-nuget-packages/"));
        Assert.DoesNotContain(check.Issues, i => i.Message.Contains("'/app'"));
    }

    [Fact]
    public void DiscoverySkipsSymlinksAndPreservesAmbiguousPorts()
    {
        File.WriteAllText(Path.Combine(root, "compose.yaml"), "services: {}");
        File.CreateSymbolicLink(Path.Combine(root, "docker-compose.yml"), "/etc/passwd");
        Assert.Equal(["compose.yaml"], RepositoryConfigurationInspector.FindComposeFiles(root));
        var check = RepositoryConfigurationInspector.InspectCompose(
            root,
            """{"services":{"web":{"expose":[80,443,"53/udp"]}}}""",
            [],
            "compose.yaml",
            "web",
            80
        );
        Assert.Equal([80, 443], Assert.Single(check.Services).Ports);
    }

    [Fact]
    public void CopyOutsideContextCannotReadHostFiles()
    {
        File.WriteAllText(Path.Combine(root, "Dockerfile"), "COPY ../private-file /app\n");
        var check = RepositoryConfigurationInspector.InspectSingle(
            root,
            ForgeDock.Domain.DeploymentMode.Dockerfile,
            ".",
            "Dockerfile",
            80
        );
        Assert.Contains(
            check.Issues,
            i => i.Severity == "warning" && i.Message.Contains("could not be inspected")
        );
    }
}
