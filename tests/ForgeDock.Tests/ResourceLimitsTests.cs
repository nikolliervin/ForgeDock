using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using System.Text.Json.Nodes;
namespace ForgeDock.Tests;
public class ResourceLimitsTests
{
    [Theory]
    [InlineData(0.1, 64, true)]
    [InlineData(32, 65536, true)]
    [InlineData(0, 512, false)]
    [InlineData(1, 63, false)]
    [InlineData(33, 512, false)]
    public void ValidatesQuotas(double cpu, int memory, bool valid) => Assert.Equal(valid, ResourceLimits.Valid(cpu, memory));
    [Fact]
    public void RequiresSustainedFreshPressureAndNormalizesCpuByQuota()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 3).Select(i => new MetricSample { Timestamp = now.AddSeconds(-30 * i), CpuPercent = 180, MemoryLimitBytes = 100, MemoryBytes = 20 }).ToArray();
        Assert.Equal("CpuPressure", ResourceLimits.Saturation(samples, 2, now));
        Assert.Null(ResourceLimits.Saturation(samples, 4, now));
        samples[1].Timestamp = now.AddMinutes(-5); Assert.Null(ResourceLimits.Saturation(samples, 2, now));
        samples[1].Timestamp = now; foreach (var sample in samples) sample.MemoryBytes = 95;
        Assert.Equal("MemoryPressure", ResourceLimits.Saturation(samples, 4, now));
        Assert.Null(ResourceLimits.Saturation(samples.Take(2).ToArray(), 2, now));
    }
    [Fact]
    public void ComposeOverridesConflictingLimitsOnRoutedServiceOnly()
    {
        var model = JsonNode.Parse("""{"services":{"app":{"mem_limit":"4g","cpus":4,"deploy":{"resources":{"limits":{"cpus":"4","memory":"4g"}}}},"db":{"mem_limit":"2g"}}}""")!.AsObject();
        ResourceLimits.ApplyCompose(model, new DeploymentSnapshot("https://github.com/team/app", "main", "Dockerfile", 8080, "/", [], ComposeService: "app", CpuLimit: 0.5, MemoryLimitMiB: 256));
        Assert.Equal(0.5, model["services"]!["app"]!["cpus"]!.GetValue<double>());
        Assert.Equal("256m", model["services"]!["app"]!["deploy"]!["resources"]!["limits"]!["memory"]!.GetValue<string>());
        Assert.Equal("2g", model["services"]!["db"]!["mem_limit"]!.GetValue<string>());
    }
    [DockerFact]
    public async Task DockerEnforcesConfiguredLimits()
    {
        var runner = new ProcessRunner(); var name = "forgedock-resource-test-" + Guid.NewGuid().ToString("N");
        Task<string> Run(params string[] args) => runner.RunAsync("docker", args, null, _ => Task.CompletedTask, default);
        try
        {
            await Run(["create", "--name", name, "--label", "io.forgedock.managed=true", .. ResourceLimits.DockerArguments(0.5, 128), "redis:7.4-alpine"]);
            Assert.Equal("134217728|500000000|on-failure|5", await Run("inspect", "--format", "{{.HostConfig.Memory}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.RestartPolicy.Name}}|{{.HostConfig.RestartPolicy.MaximumRetryCount}}", name));
        }
        finally { await Run("rm", "-f", name); }
    }
}
