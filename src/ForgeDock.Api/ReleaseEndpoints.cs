using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class ReleaseEndpoints
{
    public static void MapReleaseEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/releases", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct); if (project is null) return Results.NotFound();
            var environments = string.IsNullOrEmpty(project.ApplicationName) ? [] : await db.Projects.AsNoTracking().Where(p => p.ApplicationName == project.ApplicationName)
                .OrderBy(p => p.EnvironmentName).Select(p => new { p.Id, p.Name, p.EnvironmentName, p.HealthStatus }).ToListAsync(ct);
            var ids = environments.Select(p => p.Id).ToArray();
            var releases = await db.Deployments.AsNoTracking().Where(d => ids.Contains(d.ProjectId) && d.ImageTag != null && (d.State == DeploymentState.Running || d.State == DeploymentState.Stopped))
                .OrderByDescending(d => d.CreatedAt).Take(100).Select(d => new { d.Id, d.ProjectId, d.CommitSha, d.CreatedAt, d.Trigger }).ToListAsync(ct);
            return Results.Ok(new { project.ApplicationName, project.EnvironmentName, environments, releases });
        });
        api.MapPut("/projects/{id:guid}/releases/group", async (Guid id, EnvironmentGroup request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var application = request.ApplicationName?.Trim().ToLowerInvariant(); var name = request.EnvironmentName?.Trim().ToLowerInvariant();
            if ((!string.IsNullOrEmpty(application) || !string.IsNullOrEmpty(name)) && (!Valid(application) || !Valid(name)))
                return Results.Problem("Application and environment names must be 1–50 lowercase letters, digits, or hyphens. Leave both empty to ungroup.", statusCode: 400);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({StorageRuntime.LockId})", ct);
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            if (project.ParentProjectId != null) return Results.Conflict(new { error = "PR previews cannot join release environment groups." });
            if (!string.IsNullOrEmpty(application) && await db.Projects.AnyAsync(p => p.Id != id && p.ApplicationName == application && p.EnvironmentName == name, ct))
                return Results.Conflict(new { error = "This application already has an environment with that name." });
            project.ApplicationName = string.IsNullOrEmpty(application) ? null : application; project.EnvironmentName = string.IsNullOrEmpty(name) ? null : name;
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });
        api.MapPost("/projects/{id:guid}/releases/promote", async (Guid id, PromotionRequest request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (!request.Confirm) return Results.Problem("Confirm deployment of the selected image into this environment.", statusCode: 400);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({StorageRuntime.LockId})", ct);
            var target = await db.Projects.FindAsync([id], ct); var source = await db.Deployments.FindAsync([request.SourceDeploymentId], ct);
            if (target is null || source is null) return Results.NotFound(); var sourceProject = await db.Projects.FindAsync([source.ProjectId], ct); if (sourceProject is null) return Results.NotFound();
            if (await db.Deployments.AnyAsync(d => d.ProjectId == id && d.State != DeploymentState.Running && d.State != DeploymentState.Stopped && d.State != DeploymentState.Failed && d.State != DeploymentState.Cancelled, ct)
                || await db.Operations.AnyAsync(o => o.ProjectId == id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct)
                || await db.DatabaseBackups.AnyAsync(b => b.ProjectId == id && (b.State == "Queued" || b.State == "Running"), ct))
                return Results.Conflict(new { error = "Wait for this environment's pending work." });
            Deployment deployment;
            try { deployment = ReleasePromotion.Create(sourceProject, source, target, await db.EnvironmentVariables.Where(e => e.ProjectId == id).ToListAsync(ct)); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
            db.Deployments.Add(deployment); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Accepted($"/api/deployments/{deployment.Id}", DeploymentResponse.From(deployment));
        });
    }
    private static bool Valid(string? value) => value?.Length is >= 1 and <= 50 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
public sealed record EnvironmentGroup(string? ApplicationName, string? EnvironmentName);
public sealed record PromotionRequest(Guid SourceDeploymentId, bool Confirm);
