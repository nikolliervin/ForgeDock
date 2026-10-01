using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Tests;
public class AutomaticRollbackTests
{
    [Fact]
    public void ConsecutiveFailuresResetAndExpiredOrReplayedReleasesDoNotTrigger()
    {
        var deployment = StorageRetentionTests.Successful(Guid.NewGuid(), "image", 1);
        var running = new Deployment { PreviousDeploymentId = deployment.Id, RollbackDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning, DeploymentState.Building, DeploymentState.Starting, DeploymentState.HealthChecking, DeploymentState.Routing, DeploymentState.Running }) running.TransitionTo(state);
        Assert.False(AutomaticRollback.Observe(running, false, 2, DateTimeOffset.UtcNow)); Assert.False(AutomaticRollback.Observe(running, true, 2, DateTimeOffset.UtcNow)); Assert.Equal(0, running.HealthFailureCount);
        Assert.False(AutomaticRollback.Observe(running, false, 2, DateTimeOffset.UtcNow)); Assert.True(AutomaticRollback.Observe(running, false, 2, DateTimeOffset.UtcNow));
        Assert.Contains(deployment.Id, StorageRetention.ProtectedDeployments([], [deployment, running], 0));
        running.AutoRollbackTriggeredAt = DateTimeOffset.UtcNow; Assert.False(AutomaticRollback.Observe(running, false, 2, DateTimeOffset.UtcNow));
        var rollback = new Deployment { RollbackSourceId = deployment.Id }; AutomaticRollback.Arm(rollback, deployment.Id, new("https://example.com/app", "main", "Dockerfile", 80, "/", [], AutoRollbackEnabled: true)); Assert.Null(rollback.RollbackDeadlineAt);
        running.RollbackDeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1); Assert.False(AutomaticRollback.Observe(running, false, 1, DateTimeOffset.UtcNow));
    }
    [DockerFact]
    public async Task FailedReleaseQueuesOneRollbackAndWorkerRestoresPreviousRuntimeSnapshot()
    {
        await using var fixture = new WorkerFixture(); await fixture.Initialize();
        var project = new Project { Name = "Rollback test", RepositoryUrl = "https://github.com/example/app", ContainerPort = 8080, AutoRollbackEnabled = true, RollbackFailureThreshold = 2 };
        fixture.Projects.Add(project.Id); fixture.Db.Projects.Add(project);
        var source = StorageRetentionTests.Successful(project.Id, "", 1); source.ImageTag = $"forgedock/{project.Id:N}:{source.Id:N}"; source.ConfigurationJson = DeploymentSnapshot.Create(project, []).Serialize();
        await fixture.BuildImage(source.ImageTag); fixture.Db.Deployments.Add(source); fixture.Db.NotificationSettings.Add(new NotificationSettings { ProjectId = project.Id, Email = "test@example.com" }); await fixture.Db.SaveChangesAsync();
        Deployment Deploy(string value) => new() { ProjectId = project.Id, RollbackSourceId = source.Id, ImageTag = source.ImageTag, Trigger = "Promotion", ConfigurationJson = DeploymentSnapshot.Create(project, [new ProjectEnvironment { Name = "RELEASE_VALUE", ProtectedValue = fixture.Protector.Protect(value) }]).Serialize() };
        var first = Deploy("stable"); fixture.Db.Deployments.Add(first); await fixture.Db.SaveChangesAsync(); await fixture.Invoke("ExecuteDeployment", fixture.Db, first, CancellationToken.None);
        var next = Deploy("bad-release"); fixture.Db.Deployments.Add(next); await fixture.Db.SaveChangesAsync(); await fixture.Invoke("ExecuteDeployment", fixture.Db, next, CancellationToken.None);
        Assert.Equal(first.Id, next.PreviousDeploymentId); await fixture.Docker("stop", next.ContainerId!);
        await fixture.Invoke("MonitorApplications", fixture.Db, CancellationToken.None); Assert.False(await fixture.Db.Deployments.AnyAsync(d => d.Trigger == "AutomaticRollback"));
        await fixture.Invoke("MonitorApplications", fixture.Db, CancellationToken.None); var rollback = await fixture.Db.Deployments.SingleAsync(d => d.Trigger == "AutomaticRollback");
        Assert.Equal(first.Id, rollback.RollbackSourceId); Assert.NotNull(next.AutoRollbackTriggeredAt); Assert.Single(await fixture.Db.NotificationDeliveries.Where(n => n.Event == "AutomaticRollback").ToListAsync());
        await fixture.Invoke("MonitorApplications", fixture.Db, CancellationToken.None); Assert.Equal(1, await fixture.Db.Deployments.CountAsync(d => d.Trigger == "AutomaticRollback"));
        await fixture.Invoke("ExecuteDeployment", fixture.Db, rollback, CancellationToken.None); Assert.Equal(rollback.Id, project.ActiveDeploymentId); Assert.Null(rollback.RollbackDeadlineAt);
        Assert.Equal("stable", await fixture.Docker("exec", "forgedock-proxy", "wget", "-q", "-O", "-", $"http://{rollback.ContainerId}:8080/"));
    }
}
