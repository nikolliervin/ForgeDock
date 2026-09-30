using System.Text.Json;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed class MetricsCollector(IServiceScopeFactory scopes, ProcessRunner runner, ILogger<MetricsCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var ct = timeout.Token;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                // A transaction-scoped lock also prevents duplicate sampling across worker processes.
                var locked = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(74623020) AS \"Value\"").SingleAsync(ct);
                if (locked)
                {
                    var active = await db.Projects.Where(p => p.ActiveDeploymentId != null).ToDictionaryAsync(p => p.Id, ct);
                    var ids = await Docker(["ps", "--filter", "label=io.forgedock.managed=true", "--filter", "label=io.forgedock.project", "--no-trunc", "--format", "{{.ID}}"], ct);
                    var targets = new Dictionary<string, (Guid Project, Guid Deployment, string Service, DateTimeOffset Started)>();
                    if (!string.IsNullOrWhiteSpace(ids))
                    {
                        const string format = "{\"id\":{{json .Id}},\"project\":{{json (index .Config.Labels \"io.forgedock.project\")}},\"deployment\":{{json (index .Config.Labels \"io.forgedock.deployment\")}},\"service\":{{json (index .Config.Labels \"com.docker.compose.service\")}},\"stack\":{{json (index .Config.Labels \"com.docker.compose.project\")}},\"running\":{{.State.Running}},\"started\":{{json .State.StartedAt}}}";
                        var metadata = await Docker(new[] { "inspect", "--format", format }.Concat(ids.Split('\n', StringSplitOptions.RemoveEmptyEntries)), ct);
                        foreach (var line in metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            using var doc = JsonDocument.Parse(line); var info = doc.RootElement;
                            string Get(string key) => info.GetProperty(key).GetString() ?? "";
                            if (!info.GetProperty("running").GetBoolean() || !Guid.TryParse(Get("project"), out var projectId) || !active.TryGetValue(projectId, out var project)) continue;
                            var service = Get("service");
                            if (service.Length == 0 && Get("deployment") != project.ActiveDeploymentId.ToString()) continue;
                            if (service.Length > 0 && Get("stack") != ComposeDefinition.StackName(projectId)) continue;
                            targets[Get("id")] = (projectId, project.ActiveDeploymentId!.Value, service.Length > 0 ? service : "app", DateTimeOffset.Parse(Get("started")));
                        }
                    }
                    if (targets.Count > 0)
                    {
                        var stats = await Docker(new[] { "stats", "--no-stream", "--no-trunc", "--format", "{{json .}}" }.Concat(targets.Keys), ct);
                        var timestamp = DateTimeOffset.UtcNow;
                        foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (targets.TryGetValue(doc.RootElement.GetProperty("ID").GetString()!, out var target))
                                db.Metrics.Add(DockerMetrics.Parse(line, target.Project, target.Deployment, doc.RootElement.GetProperty("ID").GetString()!, target.Service, timestamp, target.Started));
                        }
                        await db.SaveChangesAsync(ct);
                    }
                    if (DateTimeOffset.UtcNow >= nextCleanup)
                    {
                        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                        await db.Metrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
                        nextCleanup = DateTimeOffset.UtcNow.AddMinutes(15);
                    }
                    await transaction.CommitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Application metrics collection failed; retrying next interval"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private Task<string> Docker(IEnumerable<string> arguments, CancellationToken ct) =>
        runner.RunAsync("docker", arguments, null, _ => Task.CompletedTask, ct, inheritEnvironment: false);
}
