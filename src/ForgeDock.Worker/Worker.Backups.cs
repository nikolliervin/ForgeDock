using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Worker;
public sealed partial class Worker
{
    private BackupRuntime Backups => new(Databases, configuration["ForgeDock:RuntimePath"] ?? ".runtime", configuration["ForgeDock:SecretKey"]!);
    private async Task ProcessBackups(ForgeDockDbContext db, CancellationToken ct)
    {
        foreach (var service in await db.DatabaseServices.Where(s => s.State == "Running" && s.BackupIntervalHours > 0 && s.NextBackupAt <= DateTimeOffset.UtcNow).ToListAsync(ct))
        {
            if (await db.DatabaseBackups.AnyAsync(b => b.ServiceId == service.Id && (b.State == "Queued" || b.State == "Running"), ct)) continue;
            db.DatabaseBackups.Add(new DatabaseBackup { ProjectId = service.ProjectId, ServiceId = service.Id });
            service.NextBackupAt = DateTimeOffset.UtcNow.AddHours(service.BackupIntervalHours);
        }
        await db.SaveChangesAsync(ct);
        var job = await db.DatabaseBackups.Where(b => b.State == "Queued").OrderBy(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return;
        var database = await db.DatabaseServices.FindAsync([job.ServiceId], ct);
        var project = await db.Projects.FindAsync([job.ProjectId], ct);
        Deployment? active = project?.ActiveDeploymentId is { } activeId ? await db.Deployments.FindAsync([activeId], ct) : null;
        var resume = false;
        job.State = "Running"; await db.SaveChangesAsync(ct);
        try
        {
            if (database is null || database.State != "Running") throw new InvalidOperationException();
            if (job.Kind == "Backup") job.SizeBytes = await Backups.Create(database, job.Id, ct);
            else
            {
                if (active?.ContainerId is { } container && active.State == DeploymentState.Running)
                {
                    resume = await Databases.Docker(["inspect", "--format", "{{.State.Running}}", container], ct) == "true";
                    if (resume)
                    {
                        if (active.ProtectedComposeManifest is { } manifest) await ComposeEngine.StopAsync(ReadCompose(manifest), project!.Id, false, _ => Task.CompletedTask, ct);
                        else await Databases.Docker(["stop", container], ct);
                        project!.HealthStatus = "Restoring"; await db.SaveChangesAsync(ct);
                    }
                }
                await Backups.Restore(database, job.SourceBackupId!.Value, ct);
            }
            job.State = "Completed";
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { job.State = "Failed"; job.Error = "Database operation failed. Check Docker and backup files before retrying."; logger.LogWarning("Backup job {Id} failed ({Type}).", job.Id, error.GetType().Name); }
        finally
        {
            if (resume && active?.ContainerId is { } container)
            {
                try
                {
                    if (active.ProtectedComposeManifest is { } manifest) await ComposeEngine.StartAsync(ReadCompose(manifest), project!.Id, _ => Task.CompletedTask, CancellationToken.None);
                    else await Databases.Docker(["start", container], CancellationToken.None);
                    project!.HealthStatus = "Running";
                }
                catch { project!.HealthStatus = "Unhealthy"; job.Error = "Restore finished, but the app could not restart. Redeploy it."; job.State = "Failed"; }
            }
        }
        job.FinishedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
        if (job.State == "Completed" && job.Kind == "Backup")
        {
            var retained = Math.Clamp(configuration.GetValue("ForgeDock:BackupRetentionCount", 7), 1, 100);
            var older = await db.DatabaseBackups.Where(b => b.ServiceId == job.ServiceId && b.Kind == "Backup" && b.State == "Completed")
                .OrderByDescending(b => b.CreatedAt).Skip(retained).ToListAsync(ct);
            foreach (var backup in older) { File.Delete(Backups.FilePath(backup.Id)); backup.State = "Expired"; }
            await db.SaveChangesAsync(ct);
        }
    }
}
