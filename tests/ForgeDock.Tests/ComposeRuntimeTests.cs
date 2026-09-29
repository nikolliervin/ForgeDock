using ForgeDock.Infrastructure;
using ForgeDock.Domain;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ForgeDock.Tests;

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORGEDOCK_DOCKER_TESTS") != "1")
            Skip = "Set FORGEDOCK_DOCKER_TESTS=1 with Docker/Compose and ForgeDock infrastructure running.";
    }
}

public class ComposeRuntimeTests
{
    [DockerFact]
    public async Task StackPreservesDataAcrossChangedVersionRollbackAndDeletion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ForgeDock.sln"))) directory = directory.Parent;
        var repository = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var project = Guid.NewGuid();
        var root = Path.Combine(repository, ".runtime", "compose-tests", project.ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "web"));
        foreach (var file in Directory.GetFiles(Path.Combine(repository, "examples", "compose-demo"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, Path.GetRelativePath(Path.Combine(repository, "examples", "compose-demo"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var runner = new ProcessRunner();
        var protector = new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var engine = new ComposeRuntime(runner, protector, root, "forgedock", Path.Combine(repository, ".runtime", "tools", "docker-compose"));
        var logs = new List<string>();
        Task Log(string line) { logs.Add(line); return Task.CompletedTask; }
        Task<string> Docker(params string[] arguments) => runner.RunAsync("docker", arguments, null, _ => Task.CompletedTask, CancellationToken.None);
        var snapshot = new DeploymentSnapshot("https://github.com/example/app", "main", "", 3000, "/health",
            new() { ["DEMO_LABEL"] = protector.Protect("synthetic-compose-value") }, DeploymentMode.Compose, "compose.yaml", "web");
        JsonObject? first = null;
        try
        {
            first = await engine.PrepareAsync(source, project, Guid.NewGuid(), snapshot, Log, CancellationToken.None);
            await engine.StartAsync(first, project, Log, CancellationToken.None);
            async Task<string> Page() => await Docker("exec", "forgedock-proxy", "wget", "-q", "-O", "-",
                $"http://{ComposeDefinition.ContainerName(project, "web")}:3000/");
            Assert.Contains("version 1", await Page());
            await Docker("exec", ComposeDefinition.ContainerName(project, "store"), "redis-cli", "SET", "retained", "original-data");
            var statuses = await engine.StatusAsync(project, CancellationToken.None);
            Assert.Equal(2, statuses.Count); Assert.All(statuses, status => Assert.Equal("running", status.State));
            await File.WriteAllTextAsync(Path.Combine(source, "web", "version.txt"), "version 2");
            var second = await engine.PrepareAsync(source, project, Guid.NewGuid(), snapshot, Log, CancellationToken.None);
            await engine.StartAsync(second, project, Log, CancellationToken.None);
            Assert.Contains("version 2", await Page());
            Assert.Equal("original-data", await Docker("exec", ComposeDefinition.ContainerName(project, "store"), "redis-cli", "GET", "retained"));
            await engine.StartAsync(first, project, Log, CancellationToken.None);
            Assert.Contains("version 1", await Page());
            var broken = (JsonObject)second.DeepClone();
            broken["services"]!["web"]!["command"] = new JsonArray("node", "-e", "process.exit(1)");
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(broken, project, Log, CancellationToken.None));
            await engine.StartAsync(first, project, Log, CancellationToken.None);
            Assert.Contains("version 1", await Page());
            await engine.StopAsync(first, project, false, Log, CancellationToken.None);
            Assert.All(await engine.StatusAsync(project, CancellationToken.None), status => Assert.NotEqual("running", status.State));
            await engine.StartAsync(first, project, Log, CancellationToken.None);
            Assert.Equal("original-data", await Docker("exec", ComposeDefinition.ContainerName(project, "store"), "redis-cli", "GET", "retained"));
            await engine.LogsAsync(first, project, Log, CancellationToken.None);
            Assert.DoesNotContain(logs, line => line.Contains("synthetic-compose-value"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "secrets"), "*.compose"));
            await engine.StopAsync(first, project, true, Log, CancellationToken.None);
            Assert.Empty(await engine.StatusAsync(project, CancellationToken.None));
            var volume = first["volumes"]!["data"]!["name"]!.GetValue<string>();
            var owned = await Docker("volume", "inspect", "--format", "{{index .Labels \"io.forgedock.project\"}}", volume);
            Assert.Equal(project.ToString(), owned);
            await Docker("volume", "rm", volume);
        }
        finally
        {
            if (first is not null) await engine.StopAsync(first, project, true, Log, CancellationToken.None);
        }
    }
}
