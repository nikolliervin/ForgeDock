using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using ForgeDock.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
namespace ForgeDock.Tests;
public class ResourceMonitorTests
{
    [DockerFact]
    public async Task CrashesQueueAlertsDurablyAndRespectCooldownAndDisabledMonitoring()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock");
        Assert.False(string.IsNullOrEmpty(original), "Run with scripts/with-env.sh.");
        var database = "forgedock_alert_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(original!) { Database = database };
        var admin = new NpgsqlConnectionStringBuilder(original!) { Database = "postgres" };
        await using var owner = new NpgsqlConnection(admin.ConnectionString); await owner.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", owner)) await command.ExecuteNonQueryAsync();
        var runner = new ProcessRunner(); var name = "forgedock-alert-test-" + Guid.NewGuid().ToString("N");
        Task<string> Docker(params string[] args) => runner.RunAsync("docker", args, null, _ => Task.CompletedTask, default);
        await using var provider = new ServiceCollection().AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection.ConnectionString)).BuildServiceProvider();
        try
        {
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>(); await db.Database.MigrateAsync();
            var project = new Project { Name = "Crash test", RepositoryUrl = "https://github.com/example/test", HealthStatus = "Stopped" };
            var deployment = new Deployment { ProjectId = project.Id, ContainerId = name, ConfigurationJson = DeploymentSnapshot.Create(project, []).Serialize() };
            foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning, DeploymentState.Building, DeploymentState.Starting, DeploymentState.HealthChecking, DeploymentState.Routing, DeploymentState.Running }) deployment.TransitionTo(state);
            project.ActiveDeploymentId = deployment.Id;
            db.Projects.Add(project); db.Deployments.Add(deployment);
            db.NotificationSettings.Add(new NotificationSettings { ProjectId = project.Id, Email = "test@example.com" }); await db.SaveChangesAsync();
            await Docker("create", "--name", name, "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={project.Id}", "--label", $"io.forgedock.deployment={deployment.Id}", "redis:7.4-alpine", "sh", "-c", "exit 7");
            await Docker("start", name); await Docker("wait", name);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ForgeDock:DashboardBaseUrl"] = "https://dashboard.example" }).Build();
            var monitor = new ResourceMonitor(provider.GetRequiredService<IServiceScopeFactory>(), runner, config, NullLogger<ResourceMonitor>.Instance);
            for (var i = 0; i < 4; i++) await monitor.CheckAsync(default);
            Assert.Single(await db.ResourceAlerts.AsNoTracking().ToListAsync()); var notification = Assert.Single(await db.NotificationDeliveries.AsNoTracking().ToListAsync());
            Assert.Contains(deployment.Id.ToString(), notification.Message); Assert.StartsWith("ResourceAlert:", notification.Event);
            project.ResourceAlertsEnabled = false; await db.SaveChangesAsync();
            await monitor.CheckAsync(default); Assert.Equal(1, await db.ResourceAlerts.CountAsync());
        }
        finally
        {
            await Docker("rm", "-f", name);
            NpgsqlConnection.ClearAllPools();
            await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", owner); await command.ExecuteNonQueryAsync();
        }
    }
}
