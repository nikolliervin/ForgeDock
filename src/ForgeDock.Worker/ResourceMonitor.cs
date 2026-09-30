using System.Text.Json.Nodes;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Worker;
public sealed class ResourceMonitor(IServiceScopeFactory scopes, ProcessRunner runner, IConfiguration configuration, ILogger<ResourceMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning("Resource monitor failed ({Type}).", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
    public async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(74623022) AS \"Value\"").SingleAsync(ct)) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var project in await db.Projects.Where(p => p.ActiveDeploymentId != null && p.ResourceAlertsEnabled && p.HealthStatus != "Restoring").ToListAsync(ct))
        {
            var deployment = await db.Deployments.FindAsync([project.ActiveDeploymentId!.Value], ct);
            if (deployment?.ContainerId is not { } container || deployment.State != DeploymentState.Running) continue;
            var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
            JsonNode info;
            try { info = JsonNode.Parse(await runner.RunAsync("docker", ["inspect", container], null, _ => Task.CompletedTask, ct, inheritEnvironment: false))!.AsArray()[0]!; }
            catch (InvalidOperationException) { continue; }
            var labels = info["Config"]!["Labels"]!;
            if (labels["io.forgedock.project"]?.GetValue<string>() != project.Id.ToString()) continue;
            if (snapshot.DeploymentMode != DeploymentMode.Compose && labels["io.forgedock.deployment"]?.GetValue<string>() != deployment.Id.ToString()) continue;
            var observation = await db.ResourceObservations.FindAsync([project.Id], ct);
            if (observation is null) { observation = new ResourceObservation { ProjectId = project.Id, DeploymentId = deployment.Id }; db.ResourceObservations.Add(observation); }
            if (observation.DeploymentId != deployment.Id) { observation.DeploymentId = deployment.Id; observation.RestartBaseline = 0; observation.ExitedChecks = 0; observation.OomObserved = false; }
            var state = info["State"]!; var restarts = info["RestartCount"]!.GetValue<int>(); var oom = state["OOMKilled"]!.GetValue<bool>();
            observation.ExitedChecks = !state["Running"]!.GetValue<bool>() && state["ExitCode"]!.GetValue<int>() != 0 ? observation.ExitedChecks + 1 : 0;
            string? kind = null;
            if (oom && !observation.OomObserved) kind = "OutOfMemory";
            else if (restarts - observation.RestartBaseline >= 3 || observation.ExitedChecks >= 3) kind = "RepeatedCrashes";
            else
            {
                var service = snapshot.DeploymentMode == DeploymentMode.Compose ? snapshot.ComposeService : "app";
                var samples = await db.Metrics.AsNoTracking().Where(m => m.ProjectId == project.Id && m.DeploymentId == deployment.Id && m.Service == service)
                    .OrderByDescending(m => m.Timestamp).Take(3).ToListAsync(ct);
                kind = ResourceLimits.Saturation(samples, snapshot.CpuLimit, now);
            }
            observation.OomObserved = oom;
            if (kind is null || await db.ResourceAlerts.AnyAsync(a => a.DeploymentId == deployment.Id && a.Kind == kind && a.CreatedAt > now.AddHours(-1), ct)) continue;
            var text = kind switch { "OutOfMemory" => "App was killed for exceeding its memory limit.", "RepeatedCrashes" => "App has repeatedly crashed or restarted.", "MemoryPressure" => "App memory usage stayed above 90% of its limit for three samples.", _ => "App CPU usage stayed above 90% of its configured quota for three samples." };
            var alert = new ResourceAlert { ProjectId = project.Id, DeploymentId = deployment.Id, Kind = kind, Message = text }; db.ResourceAlerts.Add(alert);
            observation.RestartBaseline = restarts;
            var settings = await db.NotificationSettings.FindAsync([project.Id], ct);
            if (settings is not null)
            {
                var message = project.Name + ": " + text + "\n" + NotificationPolicy.LogLink(configuration["ForgeDock:DashboardBaseUrl"] ?? "http://localhost:5173", project.Id, deployment.Id);
                foreach (var channel in new[] { "Slack", "Discord", "Email" })
                    if (channel == "Slack" && settings.ProtectedSlackUrl.Length > 0 || channel == "Discord" && settings.ProtectedDiscordUrl.Length > 0 || channel == "Email" && settings.Email.Length > 0)
                        db.NotificationDeliveries.Add(new NotificationDelivery { ProjectId = project.Id, DeploymentId = deployment.Id, Channel = channel, Event = "ResourceAlert:" + alert.Id, Message = message });
            }
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
}
