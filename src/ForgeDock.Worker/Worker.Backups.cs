using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Worker;
public sealed partial class Worker
{
    private BackupRuntime Backups => new(Databases, configuration["ForgeDock:RuntimePath"] ?? ".runtime", configuration["ForgeDock:SecretKey"]!);
    private DateTimeOffset nextBackupMaintenance;
    private async Task ProcessBackups(ForgeDockDbContext db, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow >= nextBackupMaintenance)
        {
            await SynchronizeRemoteBackups(db, ct);
            await ApplyBackupRetention(db, ct);
            nextBackupMaintenance = DateTimeOffset.UtcNow.AddMinutes(1);
        }
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
            if (job.Kind != "Backup" || database.Kind == DatabaseKind.MongoDb)
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
            }
            if (job.Kind == "Backup")
            {
                using var store = CreateBackupStore();
                if (store is not null) store.SetLocation(job);
                await db.SaveChangesAsync(ct);
                job.SizeBytes = await Backups.Create(database, job.Id, ct);
            }
            else
            {
                var source = await db.DatabaseBackups.SingleAsync(b => b.Id == job.SourceBackupId && b.ServiceId == job.ServiceId && b.State == "Completed", ct);
                if (!File.Exists(Backups.FilePath(source.Id)))
                {
                    using var store = CreateBackupStore() ?? throw new InvalidOperationException("Configure S3 to restore this remote backup.");
                    if (source.RemoteState != "Uploaded") throw new InvalidOperationException("No uploaded backup is available.");
                    await store.Download(source, Backups.FilePath(source.Id), ct);
                }
                await Backups.Restore(database, source.Id, ct);
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
        await SynchronizeRemoteBackups(db, ct);
        await ApplyBackupRetention(db, ct);
    }

    private S3BackupStore? CreateBackupStore()
    {
        if (!configuration.GetValue("ForgeDock:BackupS3:Enabled", false)) return null;
        return new(new S3BackupOptions(configuration["ForgeDock:BackupS3:Endpoint"] ?? "", configuration["ForgeDock:BackupS3:Region"] ?? "us-east-1",
            configuration["ForgeDock:BackupS3:Bucket"] ?? "", configuration["ForgeDock:BackupS3:Prefix"] ?? "forgedock",
            configuration["ForgeDock:BackupS3:AccessKey"] ?? "", configuration["ForgeDock:BackupS3:SecretKey"] ?? "",
            configuration.GetValue("ForgeDock:BackupS3:AllowHttp", false)));
    }

    private async Task SynchronizeRemoteBackups(ForgeDockDbContext db, CancellationToken ct)
    {
        using var store = CreateBackupStore();
        if (store is null) return;
        var backups = await db.DatabaseBackups.Where(b => b.Kind == "Backup" && b.State == "Completed"
            && (b.RemoteState == "Pending" || b.RemoteState == "Failed") && (b.RemoteNextAttemptAt == null || b.RemoteNextAttemptAt <= DateTimeOffset.UtcNow))
            .OrderBy(b => b.CreatedAt).Take(5).ToListAsync(ct);
        foreach (var backup in backups)
        {
            try
            {
                await store.Upload(backup, Backups.FilePath(backup.Id), ct);
                backup.RemoteState = "Uploaded"; backup.RemoteError = null; backup.RemoteNextAttemptAt = null;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                backup.RemoteState = "Failed"; backup.RemoteError = "Remote upload failed; the local backup is preserved. Retrying in five minutes.";
                backup.RemoteNextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5);
                logger.LogWarning("Remote backup upload {Id} failed ({Type}).", backup.Id, error.GetType().Name);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ApplyBackupRetention(ForgeDockDbContext db, CancellationToken ct)
    {
        using var store = CreateBackupStore();
        var retained = Math.Clamp(configuration.GetValue("ForgeDock:BackupRetentionCount", 7), 1, 100);
        var localRetained = Math.Clamp(configuration.GetValue("ForgeDock:BackupLocalRetentionCount", retained), 0, 100);
        var backups = await db.DatabaseBackups.Where(b => b.Kind == "Backup" && b.State == "Completed").OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        var restoring = await db.DatabaseBackups.Where(b => b.Kind == "Restore" && (b.State == "Queued" || b.State == "Running") && b.SourceBackupId != null)
            .Select(b => b.SourceBackupId!.Value).ToListAsync(ct);
        foreach (var group in backups.GroupBy(b => b.ServiceId))
        {
            var index = 0;
            foreach (var backup in group)
            {
                var position = index++;
                var action = BackupRetention.Decide(backup, position, retained, localRetained, store?.CanAccess(backup) == true, restoring.Contains(backup.Id));
                try
                {
                    if (action == BackupRetentionAction.Expire)
                    {
                        if (backup.RemoteState == "Uploaded") await store!.Delete(backup, ct);
                        File.Delete(Backups.FilePath(backup.Id)); backup.State = "Expired";
                    }
                    else if (action == BackupRetentionAction.RemoveLocal) File.Delete(Backups.FilePath(backup.Id));
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                { logger.LogWarning("Backup retention {Id} failed ({Type}); retrying next cycle.", backup.Id, error.GetType().Name); }
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
