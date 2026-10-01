using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class RollbackEndpoints
{
    /// <summary>
    /// Registers opt-in observation-window settings for future releases, leaving queued release policies
    /// immutable.
    /// </summary>
    public static void MapRollbackEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects/{id:guid}/rollback-policy",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var project = await db
                    .Projects.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == id, ct);
                if (project is null)
                    return Results.NotFound();
                var releases = await db
                    .Deployments.AsNoTracking()
                    .Where(d => d.ProjectId == id && d.RollbackDeadlineAt != null)
                    .OrderByDescending(d => d.CreatedAt)
                    .Take(20)
                    .Select(d => new
                    {
                        d.Id,
                        d.RollbackDeadlineAt,
                        d.HealthFailureCount,
                        d.AutoRollbackTriggeredAt,
                    })
                    .ToListAsync(ct);
                return Results.Ok(
                    new
                    {
                        project.AutoRollbackEnabled,
                        project.RollbackWindowMinutes,
                        project.RollbackFailureThreshold,
                        releases,
                    }
                );
            }
        );
        api.MapPut(
            "/projects/{id:guid}/rollback-policy",
            async (Guid id, RollbackPolicy request, ForgeDockDbContext db, CancellationToken ct) =>
            {
                if (
                    request.RollbackWindowMinutes is < 1 or > 60
                    || request.RollbackFailureThreshold is < 1 or > 10
                )
                    return Results.Problem(
                        "Choose a 1–60 minute window and 1–10 failed health checks.",
                        statusCode: 400
                    );
                var project = await db.Projects.FindAsync([id], ct);
                if (project is null)
                    return Results.NotFound();
                project.AutoRollbackEnabled = request.AutoRollbackEnabled;
                project.RollbackWindowMinutes = request.RollbackWindowMinutes;
                project.RollbackFailureThreshold = request.RollbackFailureThreshold;
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
        );
    }
}

public sealed record RollbackPolicy(
    bool AutoRollbackEnabled,
    int RollbackWindowMinutes,
    int RollbackFailureThreshold
);
