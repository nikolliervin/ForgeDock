using ForgeDock.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ForgeDock.Infrastructure;

/// <summary>Coordinates persisted work with destructive operations on a single host.</summary>
public static class QueueTransactions
{
    // Shared with storage's session lock: queueing an image consumer must finish before cleanup plans deletion.
    public const int LockId = 74623020;

    /// <summary>
    /// Starts a transaction and acquires the platform queue lock before reading eligibility.
    /// The caller must commit after saving; disposal rolls back and releases the lock on early returns.
    /// Acquire this before any project row lock to keep a consistent lock order across endpoints.
    /// </summary>
    public static async Task<IDbContextTransaction> BeginAsync(
        ForgeDockDbContext db,
        CancellationToken ct
    )
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({LockId})", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>Checks for stop/delete or backup/restore work that excludes new project commands. Call under the queue lock.</summary>
    public static async Task<bool> HasExclusiveWorkAsync(
        ForgeDockDbContext db,
        Guid projectId,
        CancellationToken ct
    ) =>
        await db.Operations.AnyAsync(
            o =>
                o.ProjectId == projectId
                && (
                    o.State == ProjectOperationState.Queued
                    || o.State == ProjectOperationState.Running
                ),
            ct
        )
        || await db.DatabaseBackups.AnyAsync(
            b => b.ProjectId == projectId && (b.State == "Queued" || b.State == "Running"),
            ct
        );

    /// <summary>Checks all pending resource consumers before stop/delete or database replacement. Call under the queue lock.</summary>
    public static async Task<bool> HasPendingWorkAsync(
        ForgeDockDbContext db,
        Guid projectId,
        CancellationToken ct
    ) =>
        await HasExclusiveWorkAsync(db, projectId, ct)
        || await db.Deployments.AnyAsync(
            d =>
                d.ProjectId == projectId
                && d.State != DeploymentState.Running
                && d.State != DeploymentState.Stopped
                && d.State != DeploymentState.Failed
                && d.State != DeploymentState.Cancelled,
            ct
        )
        || await db.JobRuns.AnyAsync(
            j => j.ProjectId == projectId && (j.State == "Queued" || j.State == "Running"),
            ct
        );
}
