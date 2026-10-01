using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace ForgeDock.Worker;
public sealed partial class Worker
{
    private DateTimeOffset nextStorageMaintenance;
    private async Task ProcessStorageCleanup(ForgeDockDbContext db, CancellationToken ct)
    {
        var policy = await db.StoragePolicies.SingleOrDefaultAsync(ct) ?? new StoragePolicy();
        var job = await db.StorageCleanups.Where(j => j.State == "Queued").OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null && DateTimeOffset.UtcNow >= nextStorageMaintenance)
        {
            nextStorageMaintenance = DateTimeOffset.UtcNow.AddMinutes(5);
            if (!policy.AutomaticCleanup || policy.LastCleanupAt > DateTimeOffset.UtcNow.AddDays(-1)) return;
            if (db.Entry(policy).State == EntityState.Detached) db.StoragePolicies.Add(policy);
            job = new StorageCleanup(); db.StorageCleanups.Add(job); await db.SaveChangesAsync(ct);
        }
        if (job is null) return;
        job.State = "Running"; await db.SaveChangesAsync(ct);
        try
        {
            var runtime = new StorageRuntime(runner, protector, configuration["ForgeDock:RuntimePath"] ?? ".runtime");
            job.ResultJson = JsonSerializer.Serialize(await runtime.Cleanup(db, policy, ct)); job.State = "Completed";
            if (db.Entry(policy).State == EntityState.Detached) db.StoragePolicies.Add(policy);
            policy.LastCleanupAt = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { job.State = "Failed"; job.Error = "Cleanup interrupted or failed; some artifacts may have been removed. Review the current preview before retrying."; logger.LogWarning("Storage cleanup {Id} failed ({Type}).", job.Id, error.GetType().Name); }
        job.FinishedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
    }
}
