using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Worker;
public sealed partial class Worker
{
    private async Task ObserveRelease(ForgeDockDbContext db, Project project, Deployment deployment, bool healthy, CancellationToken ct)
    {
        var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
        if (!AutomaticRollback.Observe(deployment, healthy, snapshot.RollbackFailureThreshold, DateTimeOffset.UtcNow)) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({StorageRuntime.LockId})", ct);
        if (await db.Deployments.AnyAsync(d => d.ProjectId == project.Id && d.State != DeploymentState.Running && d.State != DeploymentState.Stopped && d.State != DeploymentState.Failed && d.State != DeploymentState.Cancelled, ct)
            || await db.Operations.AnyAsync(o => o.ProjectId == project.Id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct)
            || await db.DatabaseBackups.AnyAsync(b => b.ProjectId == project.Id && (b.State == "Queued" || b.State == "Running"), ct))
        { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return; }
        var previous = await db.Deployments.SingleOrDefaultAsync(d => d.Id == deployment.PreviousDeploymentId && d.ProjectId == project.Id, ct);
        if (previous?.ImageTag is null || previous.State is not (DeploymentState.Running or DeploymentState.Stopped)) return;
        var rollback = new Deployment { ProjectId = project.Id, RollbackSourceId = previous.Id, ImageTag = previous.ImageTag,
            ConfigurationJson = previous.ConfigurationJson, ProtectedComposeManifest = previous.ProtectedComposeManifest,
            CommitSha = previous.CommitSha, CommitMessage = previous.CommitMessage, CommitAuthor = previous.CommitAuthor, Trigger = "AutomaticRollback" };
        db.Deployments.Add(rollback); deployment.AutoRollbackTriggeredAt = DateTimeOffset.UtcNow;
        db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Phase = "Runtime", Message = $"Automatic rollback queued after {deployment.HealthFailureCount} consecutive failed checks. Recovery deployment: {rollback.Id}." });
        var settings = await db.NotificationSettings.FindAsync([project.Id], ct);
        if (settings?.OnFailure == true)
        {
            var message = $"{project.Name}: automatic rollback queued after repeated release health failures.\n" + NotificationPolicy.LogLink(configuration["ForgeDock:DashboardBaseUrl"] ?? "http://localhost:5173", project.Id, rollback.Id);
            foreach (var channel in new[] { "Slack", "Discord", "Email" })
                if (channel == "Slack" ? settings.ProtectedSlackUrl.Length > 0 : channel == "Discord" ? settings.ProtectedDiscordUrl.Length > 0 : settings.Email.Length > 0)
                    db.NotificationDeliveries.Add(new NotificationDelivery { ProjectId = project.Id, DeploymentId = deployment.Id, Event = "AutomaticRollback", Channel = channel, Message = message });
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
}
