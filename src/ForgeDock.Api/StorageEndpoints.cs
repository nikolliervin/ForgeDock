using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/storage", async (ForgeDockDbContext db, CancellationToken ct) => Results.Ok(new
        {
            policy = await db.StoragePolicies.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StoragePolicy(),
            jobs = await db.StorageCleanups.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct)
        }));
        api.MapPut("/storage/policy", async (StoragePolicy request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (request.RetainedDeployments is < 1 or > 100 || request.SourceRetentionDays is < 1 or > 365 || request.LogRetentionDays is < 1 or > 365 || request.OrphanRetentionDays is < 1 or > 365)
                return Results.Problem("Retain 1–100 deployments and use 1–365 days for retention periods.", statusCode: 400);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({StorageRuntime.LockId})", ct);
            var policy = await db.StoragePolicies.SingleOrDefaultAsync(ct);
            if (policy is null) { policy = new StoragePolicy(); db.StoragePolicies.Add(policy); }
            policy.AutomaticCleanup = request.AutomaticCleanup; policy.RetainedDeployments = request.RetainedDeployments;
            policy.SourceRetentionDays = request.SourceRetentionDays; policy.LogRetentionDays = request.LogRetentionDays; policy.OrphanRetentionDays = request.OrphanRetentionDays;
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });
        api.MapGet("/storage/preview", async (ForgeDockDbContext db, SecretProtector protector, IConfiguration config, CancellationToken ct) =>
        {
            var runtime = new StorageRuntime(new ProcessRunner(), protector, config["ForgeDock:RuntimePath"] ?? ".runtime");
            return Results.Ok(await runtime.Preview(db, await db.StoragePolicies.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StoragePolicy(), ct));
        });
        api.MapPost("/storage/cleanup", async (StorageCleanupRequest request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            if (!request.Confirm) return Results.Problem("Confirm permanent removal of eligible artifacts.", statusCode: 400);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({StorageRuntime.LockId})", ct);
            if (await db.StorageCleanups.AnyAsync(j => j.State == "Queued" || j.State == "Running", ct)) return Results.Conflict(new { error = "A cleanup is already pending." });
            var job = new StorageCleanup(); db.StorageCleanups.Add(job); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Accepted("/api/storage", job);
        });
    }
}
public sealed record StorageCleanupRequest(bool Confirm);
