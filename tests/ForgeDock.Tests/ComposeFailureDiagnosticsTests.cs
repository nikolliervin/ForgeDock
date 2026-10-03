using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class ComposeFailureDiagnosticsTests
{
    [DockerFact]
    public async Task HealthCheckOutputIsCapturedBeforeCleanupAndSecretsAreRedacted()
    {
        var project = Guid.NewGuid();
        var name = ComposeDefinition.ContainerName(project, "redis");
        var runner = new ProcessRunner();
        Task<string> Docker(params string[] args) =>
            runner.RunAsync("docker", args, null, _ => Task.CompletedTask, default);
        var model = JsonNode
            .Parse(
                """{"services":{"redis":{"environment":{"PASSWORD":"diagnostic-private-secret"}}}}"""
            )!
            .AsObject();
        var runtime = new ComposeRuntime(
            runner,
            new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            Path.GetTempPath()
        );
        try
        {
            await Docker(
                "run",
                "-d",
                "--name",
                name,
                "--label",
                $"com.docker.compose.project={ComposeDefinition.StackName(project)}",
                "--label",
                "io.forgedock.managed=true",
                "--label",
                "com.docker.compose.service=redis",
                "--label",
                $"io.forgedock.project={project}",
                "--health-cmd",
                "echo 'Permission denied diagnostic-private-secret'; exit 7",
                "--health-interval",
                "1s",
                "--health-retries",
                "1",
                "redis:alpine"
            );
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (
                    (await Docker("inspect", "--format", "{{.State.Health.Status}}", name)).Trim()
                    == "unhealthy"
                )
                    break;
                await Task.Delay(200);
            }
            var logs = new List<string>();
            await runtime.LogFailureDiagnosticsAsync(
                model,
                project,
                line =>
                {
                    logs.Add(line);
                    return Task.CompletedTask;
                },
                "Failed startup diagnostic-private-secret",
                default
            );
            Assert.Contains(
                logs,
                l => l.Contains("health check exit 7") && l.Contains("Permission denied")
            );
            Assert.Contains(logs, l => l.Contains("SELinux labels"));
            Assert.DoesNotContain(logs, l => l.Contains("diagnostic-private-secret"));
        }
        finally
        {
            await Docker("rm", "-f", name);
        }
    }
}
