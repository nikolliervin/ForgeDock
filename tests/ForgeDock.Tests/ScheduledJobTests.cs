using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Tests;

public class ScheduledJobTests
{
    [Theory]
    [InlineData("", "echo hi", 60, 30, false)]
    [InlineData("Task", "", 60, 30, false)]
    [InlineData("Task", "echo hi", -1, 30, false)]
    [InlineData("Task", "echo hi", 10081, 30, false)]
    [InlineData("Task", "echo hi", 0, 30, true)]
    public void ValidatesManualAndRecurringDefinitions(
        string name,
        string command,
        int interval,
        int timeout,
        bool expected
    ) => Assert.Equal(expected, JobScheduling.Valid(name, command, interval, timeout));

    [DockerFact]
    public async Task SchedulerSnapshotsRuntimeRunsSeparatelyAndRecordsFailuresAndTimeouts()
    {
        await using var fixture = new WorkerFixture();
        await fixture.Initialize();
        var project = new Project
        {
            Name = "Jobs test",
            RepositoryUrl = "https://github.com/example/app",
            ContainerPort = 8080,
        };
        fixture.Projects.Add(project.Id);
        fixture.Db.Projects.Add(project);
        var source = StorageRetentionTests.Successful(project.Id, "", 1);
        source.ImageTag = $"forgedock/{project.Id:N}:{source.Id:N}";
        source.ConfigurationJson = DeploymentSnapshot
            .Create(
                project,
                [
                    new ProjectEnvironment
                    {
                        Name = "RELEASE_VALUE",
                        ProtectedValue = fixture.Protector.Protect("task-secret"),
                    },
                ]
            )
            .Serialize();
        await fixture.BuildImage(source.ImageTag);
        fixture.Db.Deployments.Add(source);
        project.ActiveDeploymentId = source.Id;
        fixture.Db.NotificationSettings.Add(
            new NotificationSettings { ProjectId = project.Id, Email = "test@example.com" }
        );
        var job = new ScheduledJob
        {
            ProjectId = project.Id,
            Name = "Repeat task",
            Command = "printf '%s' \"$RELEASE_VALUE\"; printf ' separate-container'",
            IntervalMinutes = 60,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        fixture.Db.ScheduledJobs.Add(job);
        await fixture.Db.SaveChangesAsync();
        await fixture.Invoke("ProcessScheduledJobs", fixture.Db, CancellationToken.None);
        var success = await fixture.Db.JobRuns.SingleAsync();
        Assert.Equal("Completed", success.State);
        Assert.Equal("[REDACTED] separate-container", success.Output);
        Assert.True(job.NextRunAt > DateTimeOffset.UtcNow);
        Assert.Equal(source.Id, project.ActiveDeploymentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Docker("inspect", success.ContainerId!)
        );
        await fixture.Invoke("ProcessScheduledJobs", fixture.Db, CancellationToken.None);
        Assert.Equal(1, await fixture.Db.JobRuns.CountAsync());
        job.Command = "echo failure; exit 7";
        var failure = JobScheduling.CreateRun(job, source, fixture.Protector);
        fixture.Db.JobRuns.Add(failure);
        await fixture.Db.SaveChangesAsync();
        await fixture.Invoke("ProcessScheduledJobs", fixture.Db, CancellationToken.None);
        Assert.Equal("Failed", failure.State);
        Assert.Equal(7, failure.ExitCode);
        Assert.Contains("failure", failure.Output);
        Assert.Single(await fixture.Db.NotificationDeliveries.ToListAsync());
        job.Command = "sleep 10; echo escaped";
        job.TimeoutSeconds = 1;
        var timed = JobScheduling.CreateRun(job, source, fixture.Protector);
        fixture.Db.JobRuns.Add(timed);
        await fixture.Db.SaveChangesAsync();
        await fixture.Invoke("ProcessScheduledJobs", fixture.Db, CancellationToken.None);
        Assert.Equal("Failed", timed.State);
        Assert.DoesNotContain("escaped", timed.Output);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Docker("inspect", timed.ContainerId!)
        );
    }
}
