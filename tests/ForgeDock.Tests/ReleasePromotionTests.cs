using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Tests;
public class ReleasePromotionTests
{
    private static (Project sourceProject, Deployment source, Project target) Release()
    {
        var sourceProject = new Project { Name = "Staging", RepositoryUrl = "https://github.com/example/app", ApplicationName = "app", EnvironmentName = "staging" };
        var target = new Project { Name = "Production", RepositoryUrl = sourceProject.RepositoryUrl, ApplicationName = "app", EnvironmentName = "production", CpuLimit = 2, ContainerPort = 80 };
        var source = StorageRetentionTests.Successful(sourceProject.Id, "forgedock/retained:version", 1);
        source.ConfigurationJson = DeploymentSnapshot.Create(sourceProject, [new ProjectEnvironment { Name = "SECRET", ProtectedValue = "source-secret" }]).Serialize();
        return (sourceProject, source, target);
    }
    [Fact]
    public void PromotionReusesImageAndSnapshotsOnlyTargetRuntimeConfiguration()
    {
        var (project, source, target) = Release(); var promoted = ReleasePromotion.Create(project, source, target, [new ProjectEnvironment { Name = "SECRET", ProtectedValue = "target-secret" }]);
        Assert.Equal(source.ImageTag, promoted.ImageTag); Assert.Equal(source.Id, promoted.RollbackSourceId); Assert.Equal("Promotion", promoted.Trigger);
        var snapshot = DeploymentSnapshot.Deserialize(promoted.ConfigurationJson); Assert.Equal("target-secret", snapshot.ProtectedEnvironment["SECRET"]); Assert.Equal(2, snapshot.CpuLimit); Assert.Equal(80, snapshot.ContainerPort);
        Assert.DoesNotContain("source-secret", promoted.ConfigurationJson); promoted.TransitionTo(DeploymentState.Preparing); promoted.TransitionTo(DeploymentState.Starting);
    }
    [Fact]
    public void InvalidGroupsMissingImagesAndComposeAreRejected()
    {
        var (project, source, target) = Release(); target.ApplicationName = "another"; Assert.Throws<InvalidOperationException>(() => ReleasePromotion.Create(project, source, target, []));
        target.ApplicationName = project.ApplicationName; target.DeploymentMode = DeploymentMode.Compose; Assert.Throws<InvalidOperationException>(() => ReleasePromotion.Create(project, source, target, []));
        target.DeploymentMode = DeploymentMode.Auto; source.ImageTag = null; Assert.Throws<InvalidOperationException>(() => ReleasePromotion.Create(project, source, target, []));
    }
    [DockerFact]
    public async Task WorkerPromotesExactImageAndServesTargetRuntimeValuesWithoutBuild()
    {
        await using var fixture = new WorkerFixture(); await fixture.Initialize();
        var (sourceProject, source, target) = Release(); target.ContainerPort = 8080; fixture.Projects.AddRange([sourceProject.Id, target.Id]);
        source.ImageTag = $"forgedock/{sourceProject.Id:N}:{source.Id:N}"; await fixture.BuildImage(source.ImageTag);
        fixture.Db.Projects.AddRange(sourceProject, target); fixture.Db.Deployments.Add(source); await fixture.Db.SaveChangesAsync();
        var promoted = ReleasePromotion.Create(sourceProject, source, target, [new ProjectEnvironment { Name = "RELEASE_VALUE", ProtectedValue = fixture.Protector.Protect("production-value") }]);
        fixture.Db.Deployments.Add(promoted); await fixture.Db.SaveChangesAsync(); await fixture.Invoke("ExecuteDeployment", fixture.Db, promoted, CancellationToken.None);
        Assert.Equal(DeploymentState.Running, promoted.State); Assert.Equal(promoted.Id, target.ActiveDeploymentId);
        var image = await fixture.Docker("inspect", "--format", "{{.Image}}", promoted.ContainerId!);
        Assert.Equal(await fixture.Docker("image", "inspect", "--format", "{{.Id}}", source.ImageTag), image);
        string response = "";
        for (var attempt = 0; ; attempt++)
        {
            try { response = await fixture.Docker("exec", "forgedock-proxy", "wget", "-q", "-O", "-", "--header", $"Host: {target.Id:N}.localhost", "http://127.0.0.1/"); break; }
            catch (InvalidOperationException) when (attempt < 20) { await Task.Delay(250); }
        }
        Assert.Equal("production-value", response);
        Assert.False(Directory.Exists(Path.Combine(fixture.RuntimeRoot, "sources", promoted.Id.ToString("N"))));
        Assert.DoesNotContain(await fixture.Db.Logs.Where(l => l.DeploymentId == promoted.Id).ToListAsync(), l => l.Message.Contains("Stage: Building"));
    }
}
