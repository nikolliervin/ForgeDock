using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Tests;

public class DeploymentFailureTests
{
    [DockerFact]
    public async Task PreparationErrorsAppearInBuildLogsAndBothErrorSurfacesRedactSecrets()
    {
        await using var fixture = new WorkerFixture();
        await fixture.Initialize();
        var project = new Project { Name = "Failure logs", RepositoryUrl = "https://github.com/example/app" };
        fixture.Db.Projects.Add(project);
        var deployment = new Deployment
        {
            ProjectId = project.Id,
            ConfigurationJson = DeploymentSnapshot.Create(project, [new ProjectEnvironment
            {
                ProjectId = project.Id,
                Name = "PASSWORD",
                ProtectedValue = fixture.Protector.Protect("private-test-password"),
            }]).Serialize(),
        };
        deployment.TransitionTo(DeploymentState.Preparing);
        deployment.TransitionTo(DeploymentState.Cloning);
        deployment.TransitionTo(DeploymentState.Building);
        fixture.Db.Deployments.Add(deployment);
        await fixture.Db.SaveChangesAsync();
        await fixture.Invoke("RecordDeploymentFailure", fixture.Db, deployment,
            new InvalidOperationException("Configured Compose file is missing: private-test-password"), CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Deployments.SingleAsync(d => d.Id == deployment.Id);
        var log = await fixture.Db.Logs.SingleAsync(l => l.DeploymentId == deployment.Id);
        Assert.Equal(DeploymentState.Failed, saved.State);
        Assert.Equal("Build", log.Phase);
        Assert.Contains("Configured Compose file is missing", log.Message);
        Assert.DoesNotContain("private-test-password", log.Message);
        Assert.DoesNotContain("private-test-password", saved.Error!);
        Assert.Equal($"Deployment failed: {saved.Error}", log.Message);
    }
}
