using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/backups", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            !await db.Projects.AnyAsync(p => p.Id == id, ct) ? Results.NotFound() : Results.Ok(await db.DatabaseBackups.AsNoTracking().Where(b => b.ProjectId == id).OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)));
        api.MapPut("/projects/{id:guid}/databases/{serviceId:guid}/schedule", async (Guid id, Guid serviceId, BackupSchedule request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (request.IntervalHours is < 0 or > 168) return Results.Problem("Choose 0 to disable or 1–168 hours.", statusCode: 400);
            var service = await db.DatabaseServices.SingleOrDefaultAsync(s => s.ProjectId == id && s.Id == serviceId, ct);
            if (service is null) return Results.NotFound();
            service.BackupIntervalHours = request.IntervalHours; service.NextBackupAt = request.IntervalHours == 0 ? null : DateTimeOffset.UtcNow.AddHours(request.IntervalHours);
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapPost("/projects/{id:guid}/databases/{serviceId:guid}/backups", async (Guid id, Guid serviceId, ForgeDockDbContext db, CancellationToken ct) => await Queue(id, serviceId, null, db, ct));
        api.MapPost("/projects/{id:guid}/backups/{backupId:guid}/restore", async (Guid id, Guid backupId, RestoreRequest request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (!request.Confirm) return Results.Problem("Confirm that current database data will be replaced.", statusCode: 400);
            var backup = await db.DatabaseBackups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.ProjectId == id && b.Kind == "Backup" && b.State == "Completed", ct);
            return backup is null ? Results.NotFound() : await Queue(id, backup.ServiceId, backupId, db, ct);
        });
    }
    private static async Task<IResult> Queue(Guid id, Guid serviceId, Guid? source, ForgeDockDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Projects.FromSqlInterpolated($"SELECT * FROM \"Projects\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct) is null) return Results.NotFound();
        var service = await db.DatabaseServices.SingleOrDefaultAsync(s => s.ProjectId == id && s.Id == serviceId, ct);
        if (service is null) return Results.NotFound();
        if (service.State != "Running") return Results.Conflict(new { error = "Start the database before backing up or restoring." });
        if (await db.DatabaseBackups.AnyAsync(b => b.ProjectId == id && (b.State == "Queued" || b.State == "Running"), ct)
            || await db.Operations.AnyAsync(o => o.ProjectId == id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct)
            || await db.Deployments.AnyAsync(d => d.ProjectId == id && d.State != DeploymentState.Running && d.State != DeploymentState.Stopped && d.State != DeploymentState.Failed && d.State != DeploymentState.Cancelled, ct))
            return Results.Conflict(new { error = "Wait for the current deployment, backup, or project operation." });
        var job = new DatabaseBackup { ProjectId = id, ServiceId = serviceId, Kind = source is null ? "Backup" : "Restore", SourceBackupId = source };
        db.DatabaseBackups.Add(job); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Accepted($"/api/projects/{id}/backups", job);
    }
}
public sealed record BackupSchedule(int IntervalHours);
public sealed record RestoreRequest(bool Confirm);
