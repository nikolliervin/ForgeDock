using ForgeDock.Domain;
using ForgeDock.Infrastructure;
namespace ForgeDock.Tests;
public class StorageRetentionTests
{
    public static Deployment Successful(Guid project, string image, int days)
    {
        var deployment = new Deployment { ProjectId = project, ImageTag = image, CreatedAt = DateTimeOffset.UtcNow.AddDays(-days) };
        foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning, DeploymentState.Building, DeploymentState.Starting, DeploymentState.HealthChecking, DeploymentState.Routing, DeploymentState.Running }) deployment.TransitionTo(state);
        deployment.TransitionTo(DeploymentState.Stopped); return deployment;
    }
    [Fact]
    public void ActiveAndPendingRollbackReferencesSurviveRetention()
    {
        var project = new Project(); var old = Successful(project.Id, "old", 20); var latest = Successful(project.Id, "latest", 1); var active = Successful(project.Id, "active", 30);
        var queued = new Deployment { ProjectId = project.Id, RollbackSourceId = old.Id, ImageTag = old.ImageTag }; project.ActiveDeploymentId = active.Id;
        var protectedIds = StorageRetention.ProtectedDeployments([project], [old, latest, active, queued], 1);
        Assert.Contains(old.Id, protectedIds); Assert.Contains(active.Id, protectedIds); Assert.Contains(queued.Id, protectedIds); Assert.Contains(latest.Id, protectedIds);
        queued.TransitionTo(DeploymentState.Cancelled); protectedIds = StorageRetention.ProtectedDeployments([project], [old, latest, active, queued], 1);
        Assert.DoesNotContain(old.Id, protectedIds); Assert.DoesNotContain(queued.Id, protectedIds);
    }
    [Theory]
    [InlineData("forgedock/auto-smoke:local")]
    [InlineData("postgres:17-alpine")]
    [InlineData("forgedock/not-a-project:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IgnoresUnownedImageNames(string image) => Assert.False(StorageRetention.ParseImage(image, out _, out _));
    [Fact]
    public void ParsesSingleAndComposeDeploymentTags()
    {
        var project = Guid.NewGuid(); var deployment = Guid.NewGuid();
        foreach (var tag in new[] { $"forgedock/{project:N}:{deployment:N}", ComposeDefinition.ImageName(project, deployment, "web") })
        { Assert.True(StorageRetention.ParseImage(tag, out var p, out var d)); Assert.Equal(project, p); Assert.Equal(deployment, d); }
    }
    [Fact]
    public void DiskUsageDoesNotFollowSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "file"), "123"); Directory.CreateSymbolicLink(Path.Combine(root, "outside"), "/usr"); Assert.Equal(3, StorageRetention.DirectoryBytes(root)); }
        finally { Directory.Delete(root, true); }
    }
}
