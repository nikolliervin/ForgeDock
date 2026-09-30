using ForgeDock.Domain;
using ForgeDock.Application;

namespace ForgeDock.Tests;

public class DeploymentTests
{
    [Fact]
    public void DeploymentRequiresEveryStageBeforeRunning()
    {
        var deployment = new Deployment();
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Running));
        foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning,
            DeploymentState.Building, DeploymentState.Starting, DeploymentState.HealthChecking,
            DeploymentState.Routing, DeploymentState.Running, DeploymentState.Stopped })
            deployment.TransitionTo(state);
        Assert.Equal(DeploymentState.Stopped, deployment.State);
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Preparing));
    }

    [Fact]
    public void FailureRequiresReasonAndIsTerminal()
    {
        var deployment = new Deployment();
        Assert.Throws<ArgumentException>(() => deployment.TransitionTo(DeploymentState.Failed));
        deployment.TransitionTo(DeploymentState.Failed, "Repository unavailable.");
        Assert.Equal("Repository unavailable.", deployment.Error);
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Preparing));
    }

    [Fact]
    public void OnlyRollbackCanSkipBuild()
    {
        var deployment = new Deployment();
        deployment.TransitionTo(DeploymentState.Preparing);
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Starting));
        deployment.RollbackSourceId = Guid.NewGuid();
        deployment.TransitionTo(DeploymentState.Starting);
    }

    [Fact]
    public void QueuedCancellationIsTerminalAndDoesNotPretendWorkStarted()
    {
        var deployment = new Deployment();
        deployment.TransitionTo(DeploymentState.Cancelled);
        Assert.Equal(DeploymentState.Queued, deployment.LastStage);
        Assert.Null(deployment.StartedAt);
        Assert.NotNull(deployment.FinishedAt);
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Preparing));
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Failed, "Invalid cancellation"));
    }

    [Fact]
    public void FailurePreservesItsActualStageAndDuration()
    {
        var deployment = new Deployment();
        deployment.TransitionTo(DeploymentState.Preparing);
        deployment.TransitionTo(DeploymentState.Cloning);
        deployment.TransitionTo(DeploymentState.Building);
        Assert.Throws<InvalidOperationException>(() => deployment.TransitionTo(DeploymentState.Cancelled));
        deployment.TransitionTo(DeploymentState.Failed, "Build failed.");
        Assert.Equal(DeploymentState.Building, deployment.LastStage);
        Assert.NotNull(deployment.StartedAt);
        Assert.True(deployment.FinishedAt >= deployment.StartedAt);
    }

    [Fact]
    public void StoppingADeploymentDoesNotExtendItsBuildDuration()
    {
        var deployment = new Deployment();
        foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning, DeploymentState.Building,
            DeploymentState.Starting, DeploymentState.HealthChecking, DeploymentState.Routing, DeploymentState.Running })
            deployment.TransitionTo(state);
        var finished = deployment.FinishedAt;
        deployment.TransitionTo(DeploymentState.Stopped);
        Assert.Equal(finished, deployment.FinishedAt);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("main")]
    [InlineData("abc123")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public void SpecificCommitsRequireFullHexadecimalShas(string value) => Assert.False(ProjectConfiguration.IsCommitSha(value));

    [Fact]
    public void FullGitCommitShaIsAccepted() => Assert.True(ProjectConfiguration.IsCommitSha(new string('a', 40)));

    [Theory]
    [InlineData("../Dockerfile", "main", "https://github.com/example/app")]
    [InlineData("Dockerfile", "--upload-pack=evil", "https://github.com/example/app")]
    [InlineData("Dockerfile", "main", "https://user:secret@github.com/example/app")]
    [InlineData("Dockerfile", "main", "https://127.0.0.1/app")]
    [InlineData("Dockerfile", "main", "file:///tmp/app")]
    public void RejectsUnsafeConfiguration(string dockerfile, string branch, string repository)
    {
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", repository, branch, dockerfile, 8080, "/"));
    }

    [Fact]
    public void AcceptsPublicRepositoryConfiguration() => Assert.Empty(ProjectConfiguration.Validate(
        "Demo", "https://github.com/example/app.git", "feature/demo", "src/Dockerfile", 8080, "/health"));
}
