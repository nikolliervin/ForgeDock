using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class HookEndpoints
{
    public static void MapHookEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/hooks", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct); if (project is null) return Results.NotFound();
            var executions = await db.DeploymentHooks.AsNoTracking().Where(h => db.Deployments.Any(d => d.Id == h.DeploymentId && d.ProjectId == id)).OrderByDescending(h => h.StartedAt).Take(30).ToListAsync(ct);
            return Results.Ok(new { project.PreDeployCommand, project.PostDeployCommand, project.HookTimeoutSeconds, executions });
        });
        api.MapPut("/projects/{id:guid}/hooks", async (Guid id, HookSettings request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (!TimedContainerCommand.Valid(request.PreDeployCommand, request.HookTimeoutSeconds) || !TimedContainerCommand.Valid(request.PostDeployCommand, request.HookTimeoutSeconds))
                return Results.Problem("Commands must be at most 4096 characters without null bytes; timeout must be 1–900 seconds.", statusCode: 400);
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            project.PreDeployCommand = request.PreDeployCommand; project.PostDeployCommand = request.PostDeployCommand; project.HookTimeoutSeconds = request.HookTimeoutSeconds;
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
    }
}
public sealed record HookSettings(string PreDeployCommand, string PostDeployCommand, int HookTimeoutSeconds);
