using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class ComposeConfigurationTests
{
    [Theory]
    [InlineData("../compose.yaml", "web")]
    [InlineData("/etc/compose.yaml", "web")]
    [InlineData("compose.yaml", "")]
    [InlineData("compose.yaml", "--unsafe")]
    public void RejectsUnsafeComposeConfiguration(string file, string service) =>
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "",
            3000, "/", DeploymentMode.Compose, file, service));

    [Fact]
    public void ComposeDoesNotRequireADockerfile() => Assert.Empty(ProjectConfiguration.Validate("Demo",
        "https://github.com/example/app", "main", "", 3000, "/", DeploymentMode.Compose, "deploy/compose.yaml", "web"));

    [Fact]
    public void ExistingSnapshotsDefaultToDockerfileMode()
    {
        var snapshot = DeploymentSnapshot.Deserialize("""
            {"RepositoryUrl":"https://github.com/example/app","Branch":"main","Dockerfile":"Dockerfile",
             "ContainerPort":8080,"HealthPath":"/","ProtectedEnvironment":{}}
            """);
        Assert.Equal(DeploymentMode.Dockerfile, snapshot.DeploymentMode);
    }

    [Fact]
    public void ComposeSnapshotPreservesStackSelection()
    {
        var snapshot = DeploymentSnapshot.Create(new Project { DeploymentMode = DeploymentMode.Compose,
            ComposeFile = "deploy/compose.yml", ComposeService = "frontend" }, []);
        var restored = DeploymentSnapshot.Deserialize(snapshot.Serialize());
        Assert.Equal(DeploymentMode.Compose, restored.DeploymentMode);
        Assert.Equal("frontend", restored.ComposeService);
        Assert.Equal("deploy/compose.yml", restored.ComposeFile);
    }
}
