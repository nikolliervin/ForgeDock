using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class ResourceEndpoints
{
    public static void MapResourceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/resources", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            var alerts = await db.ResourceAlerts.AsNoTracking().Where(a => a.ProjectId == id).OrderByDescending(a => a.CreatedAt).Take(30).ToListAsync(ct);
            return Results.Ok(new { project.CpuLimit, project.MemoryLimitMiB, alertsEnabled = project.ResourceAlertsEnabled, alerts });
        });
        api.MapPut("/projects/{id:guid}/resources", async (Guid id, ResourceRequest request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (!ResourceLimits.Valid(request.CpuLimit, request.MemoryLimitMiB)) return Results.Problem("CPU must be 0.1–32 cores and memory 64–65536 MiB.", statusCode: 400);
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            project.CpuLimit = request.CpuLimit; project.MemoryLimitMiB = request.MemoryLimitMiB; project.ResourceAlertsEnabled = request.AlertsEnabled;
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
    }
}
public sealed record ResourceRequest(double CpuLimit, int MemoryLimitMiB, bool AlertsEnabled = true);
