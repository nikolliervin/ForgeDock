using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Tests;
public class DeploymentHookTests
{
    [Theory]
    [InlineData("echo ok", 1, true)]
    [InlineData("", 900, true)]
    [InlineData("echo ok", 0, false)]
    [InlineData("echo ok", 901, false)]
    [InlineData("bad\0command", 120, false)]
    public void ValidatesCommandsAndTimeouts(string command, int seconds, bool expected) => Assert.Equal(expected, TimedContainerCommand.Valid(command, seconds));
    [Fact]
    public void RedactsMultilineSecretsAndSnapshotsHookSettings()
    {
        Assert.DoesNotContain("secret", TimedContainerCommand.Redact("first-secret\nsecond-secret", ["first-secret\nsecond-secret"]));
        var project = new Project { PreDeployCommand = "echo first", PostDeployCommand = "echo second", HookTimeoutSeconds = 90 }; var snapshot = DeploymentSnapshot.Create(project, []);
        project.PreDeployCommand = "changed"; Assert.Equal("echo first", DeploymentSnapshot.Deserialize(snapshot.Serialize()).PreDeployCommand);
        Assert.Contains("'\\''", TimedContainerCommand.ShellCommand("echo 'quoted'", 10));
    }
    [DockerFact]
    public async Task HooksExecuteRedactAndRecoverRouteOnFailureAndEnforceTimeout()
    {
        await using var fixture = new WorkerFixture(); await fixture.Initialize();
        var project = new Project { Name = "Hook test", RepositoryUrl = "https://github.com/example/app", ContainerPort = 8080 };
        fixture.Projects.Add(project.Id); fixture.Db.Projects.Add(project);
        var source = StorageRetentionTests.Successful(project.Id, "", 1); source.ImageTag = $"forgedock/{project.Id:N}:{source.Id:N}"; source.ConfigurationJson = DeploymentSnapshot.Create(project, []).Serialize();
        await fixture.BuildImage(source.ImageTag); fixture.Db.Deployments.Add(source); await fixture.Db.SaveChangesAsync();
        project.PreDeployCommand = "printf '%s' \"$RELEASE_VALUE\"; printf 'hook-value' > /www/index.html"; project.PostDeployCommand = "test -f /www/index.html";
        Deployment Deploy() => new() { ProjectId = project.Id, RollbackSourceId = source.Id, ImageTag = source.ImageTag, Trigger = "Promotion", ConfigurationJson = DeploymentSnapshot.Create(project, [new ProjectEnvironment { Name = "RELEASE_VALUE", ProtectedValue = fixture.Protector.Protect("runtime-secret") }]).Serialize() };
        var good = Deploy(); fixture.Db.Deployments.Add(good); await fixture.Db.SaveChangesAsync(); await fixture.Invoke("ExecuteDeployment", fixture.Db, good, CancellationToken.None);
        var hooks = await fixture.Db.DeploymentHooks.OrderBy(h => h.StartedAt).ToListAsync(); Assert.Equal(2, hooks.Count); Assert.All(hooks, h => Assert.Equal("Completed", h.State)); Assert.Equal("[REDACTED]", hooks[0].Output);
        Assert.Equal("hook-value", await fixture.Docker("exec", "forgedock-proxy", "wget", "-q", "-O", "-", $"http://{good.ContainerId}:8080/"));
        var route = Path.Combine(fixture.RuntimeRoot, "routes", $"{project.Id:N}.conf"); var previous = await File.ReadAllTextAsync(route);
        project.PostDeployCommand = "echo failed; exit 7"; var failed = Deploy(); fixture.Db.Deployments.Add(failed); await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Invoke("ExecuteDeployment", fixture.Db, failed, CancellationToken.None));
        Assert.Equal(good.Id, project.ActiveDeploymentId); Assert.Equal(previous, await File.ReadAllTextAsync(route));
        Assert.Equal(7, (await fixture.Db.DeploymentHooks.SingleAsync(h => h.DeploymentId == failed.Id && h.Phase == "AfterRoute")).ExitCode);
        failed.TransitionTo(DeploymentState.Failed, "Expected hook failure"); await fixture.Db.SaveChangesAsync();
        project.PreDeployCommand = "sleep 10; echo unexpected"; project.PostDeployCommand = ""; project.HookTimeoutSeconds = 1;
        var timed = Deploy(); fixture.Db.Deployments.Add(timed); await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Invoke("ExecuteDeployment", fixture.Db, timed, CancellationToken.None));
        var timeout = await fixture.Db.DeploymentHooks.SingleAsync(h => h.DeploymentId == timed.Id); Assert.Equal("Failed", timeout.State); Assert.NotEqual(0, timeout.ExitCode); Assert.DoesNotContain("unexpected", timeout.Output);
        Assert.Equal(good.Id, project.ActiveDeploymentId); Assert.Equal(previous, await File.ReadAllTextAsync(route));
    }
}
