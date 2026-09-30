using System.Text.Json.Nodes;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public sealed class ContainerConsoleTests
{
    private static readonly Guid Project = Guid.NewGuid(), Deployment = Guid.NewGuid();
    private static string Inspection(Action<JsonObject>? mutate = null)
    {
        var container = new JsonObject
        {
            ["Id"] = new string('a', 64),
            ["Config"] = new JsonObject { ["Image"] = "forgedock/test:active", ["Labels"] = new JsonObject
            {
                ["io.forgedock.managed"] = "true", ["io.forgedock.project"] = Project.ToString(),
                ["io.forgedock.deployment"] = Deployment.ToString()
            } },
            ["State"] = new JsonObject { ["Running"] = true, ["Paused"] = false },
            ["HostConfig"] = new JsonObject { ["Privileged"] = false, ["PidMode"] = "", ["NetworkMode"] = "forgedock" },
            ["Mounts"] = new JsonArray()
        };
        mutate?.Invoke(container);
        return new JsonArray(container).ToJsonString();
    }

    [Fact]
    public void ResolvesImmutableIdForOwnedRunningContainer() => Assert.Equal(new string('a', 64),
        ContainerConsole.VerifyContainer(Inspection(), Project, Deployment, false, "forgedock/test:active"));

    [Fact]
    public void RejectsOtherProject() => Assert.Throws<InvalidOperationException>(() =>
        ContainerConsole.VerifyContainer(Inspection(), Guid.NewGuid(), Deployment, false));

    [Fact]
    public void RejectsOtherDeployment() => Assert.Throws<InvalidOperationException>(() =>
        ContainerConsole.VerifyContainer(Inspection(), Project, Guid.NewGuid(), false));

    [Fact]
    public void RejectsReplacedComposeImage() => Assert.Throws<InvalidOperationException>(() =>
        ContainerConsole.VerifyContainer(Inspection(), Project, Deployment, true, "forgedock/test:previous"));

    [Theory]
    [InlineData("stopped")]
    [InlineData("paused")]
    [InlineData("privileged")]
    [InlineData("host-pid")]
    [InlineData("host-network")]
    [InlineData("bind")]
    public void RejectsUnavailableOrHostAccessibleContainers(string kind)
    {
        var inspection = Inspection(c =>
        {
            switch (kind)
            {
                case "stopped": c["State"]!["Running"] = false; break;
                case "paused": c["State"]!["Paused"] = true; break;
                case "privileged": c["HostConfig"]!["Privileged"] = true; break;
                case "host-pid": c["HostConfig"]!["PidMode"] = "host"; break;
                case "host-network": c["HostConfig"]!["NetworkMode"] = "host"; break;
                case "bind": c["Mounts"]!.AsArray().Add(new JsonObject { ["Type"] = "bind" }); break;
            }
        });
        Assert.Throws<InvalidOperationException>(() => ContainerConsole.VerifyContainer(inspection, Project, Deployment, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("pwd\0ls")]
    public void RejectsInvalidCommands(string? command) => Assert.False(ContainerConsole.ValidCommand(command));

    [Fact]
    public void BoundsCommandLengthAndSupportsShellSyntax()
    {
        Assert.False(ContainerConsole.ValidCommand(new string('x', 4097)));
        Assert.True(ContainerConsole.ValidCommand("cd /app && ls | head"));
    }

    [Fact]
    public async Task RealDockerCommandRunsInsideContainerWithBoundedOutput()
    {
        if (Environment.GetEnvironmentVariable("FORGEDOCK_CONSOLE_DOCKER_TESTS") != "1") return;
        var runner = new ProcessRunner();
        var container = await runner.RunAsync("docker", ["run", "-d", "--network", "none", "--label", "io.forgedock.managed=true",
            "--label", $"io.forgedock.project={Project}", "--label", $"io.forgedock.deployment={Deployment}",
            "alpine:3.22", "sleep", "120"], null, _ => Task.CompletedTask, CancellationToken.None, false);
        try
        {
            var inspection = await runner.RunAsync("docker", ["inspect", container], null, _ => Task.CompletedTask, CancellationToken.None, false);
            var id = ContainerConsole.VerifyContainer(inspection, Project, Deployment, false);
            var result = await ContainerConsole.ExecuteAsync(id, "printf 'inside-container'; printf 'stderr' >&2; exit 7", CancellationToken.None);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("inside-container", result.Output);
            Assert.Contains("stderr", result.Output);
            Assert.False(result.Truncated);
            var large = await ContainerConsole.ExecuteAsync(id, "head -c 70000 /dev/zero | tr '\\000' x", CancellationToken.None);
            Assert.True(large.Truncated);
            Assert.Equal(65536, large.Output.Length);
        }
        finally { await runner.RunAsync("docker", ["rm", "-f", container], null, _ => Task.CompletedTask, CancellationToken.None, false); }
    }
}
