using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class DeploymentEndpoints
{
    /// <summary>
    /// Registers immutable deployment snapshots, retained-image replay, bounded log cursors, and queued-only
    /// cancellation. Cancellation uses an atomic state update against the worker concurrency token.
    /// </summary>
    public static void MapDeploymentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(
            "/projects/{id:guid}/deployments",
            async (
                Guid id,
                DeploymentRequest request,
                ForgeDockDbContext db,
                CancellationToken ct
            ) =>
            {
                await using var storageTransaction = await QueueTransactions.BeginAsync(db, ct);
                if (
                    request.CommitSha is not null
                    && !ProjectConfiguration.IsCommitSha(request.CommitSha)
                )
                    return Results.Problem(
                        "Commit SHA must contain exactly 40 hexadecimal characters.",
                        statusCode: 400
                    );
                var project = await db.Projects.FindAsync([id], ct);
                if (project is null)
                    return Results.NotFound();
                if (await QueueTransactions.HasExclusiveWorkAsync(db, id, ct))
                    return Results.Conflict(
                        new { error = "Wait for the pending project operation or backup." }
                    );
                var environment = await db
                    .EnvironmentVariables.Where(e => e.ProjectId == id)
                    .ToListAsync(ct);
                var deployment = new Deployment
                {
                    ProjectId = id,
                    RequestedCommit = request.CommitSha?.ToLowerInvariant(),
                    ConfigurationJson = DeploymentSnapshot.Create(project, environment).Serialize(),
                };
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);
                await storageTransaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/deployments/{deployment.Id}",
                    DeploymentResponse.From(deployment)
                );
            }
        );
        api.MapGet(
            "/projects/{id:guid}/deployments",
            async (Guid id, int? limit, ForgeDockDbContext db, CancellationToken ct) =>
                (
                    await db
                        .Deployments.AsNoTracking()
                        .Where(d => d.ProjectId == id)
                        .OrderByDescending(d => d.CreatedAt)
                        .Take(Math.Clamp(limit ?? 50, 1, 100))
                        .ToListAsync(ct)
                ).Select(DeploymentResponse.From)
        );
        api.MapGet(
            "/deployments/{id:guid}",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct)
                    is { } deployment
                    ? Results.Ok(DeploymentResponse.From(deployment))
                    : Results.NotFound()
        );
        api.MapGet(
            "/deployments/{id:guid}/topology",
            async (Guid id, ForgeDockDbContext db, SecretProtector protector, CancellationToken ct) =>
            {
                var deployment = await db.Deployments.AsNoTracking()
                    .SingleOrDefaultAsync(d => d.Id == id, ct);
                if (deployment is null)
                    return Results.NotFound();
                var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
                return Results.Ok(
                    ComposeTopology.Read(
                        deployment.ProtectedComposeManifest is { } manifest
                            ? protector.Unprotect(manifest)
                            : null,
                        snapshot.ComposeService
                    )
                );
            }
        );
        api.MapGet(
            "/deployments/{id:guid}/logs",
            async (Guid id, long? after, ForgeDockDbContext db, CancellationToken ct) =>
                await db
                    .Logs.AsNoTracking()
                    .Where(l => l.DeploymentId == id && l.Id > (after ?? 0))
                    .OrderBy(l => l.Id)
                    .Take(500)
                    .Select(l => new
                    {
                        l.Id,
                        l.Timestamp,
                        l.Message,
                        l.Phase,
                    })
                    .ToListAsync(ct)
        );
        api.MapPost(
            "/deployments/{id:guid}/redeploy",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                await using var storageTransaction = await QueueTransactions.BeginAsync(db, ct);
                var source = await db
                    .Deployments.AsNoTracking()
                    .SingleOrDefaultAsync(d => d.Id == id, ct);
                if (source is null)
                    return Results.NotFound();
                if (await QueueTransactions.HasExclusiveWorkAsync(db, source.ProjectId, ct))
                    return Results.Conflict(
                        new { error = "Wait for the pending project operation or backup." }
                    );
                var deployment = new Deployment
                {
                    ProjectId = source.ProjectId,
                    RequestedCommit = source.CommitSha ?? source.RequestedCommit,
                    ConfigurationJson = source.ConfigurationJson,
                };
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);
                await storageTransaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/deployments/{deployment.Id}",
                    DeploymentResponse.From(deployment)
                );
            }
        );
        api.MapPost(
            "/deployments/{id:guid}/cancel",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                // Atomic update races safely with the worker's state concurrency token.
                var now = DateTimeOffset.UtcNow;
                var changed = await db
                    .Deployments.Where(d => d.Id == id && d.State == DeploymentState.Queued)
                    .ExecuteUpdateAsync(
                        update =>
                            update
                                .SetProperty(d => d.State, DeploymentState.Cancelled)
                                .SetProperty(d => d.LastStage, DeploymentState.Queued)
                                .SetProperty(d => d.UpdatedAt, now)
                                .SetProperty(d => d.FinishedAt, now),
                        ct
                    );
                if (changed == 0)
                    return await db.Deployments.AnyAsync(d => d.Id == id, ct)
                        ? Results.Conflict(
                            new
                            {
                                error = "Only queued deployments can be cancelled. This deployment has already started or ended.",
                            }
                        )
                        : Results.NotFound();
                return Results.Ok(new { state = DeploymentState.Cancelled });
            }
        );
        api.MapDelete(
            "/deployments/{id:guid}",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var deployment = await db.Deployments.FindAsync([id], ct);
                if (deployment is null)
                    return Results.NotFound();
                if (
                    deployment.State is not (DeploymentState.Failed or DeploymentState.Cancelled)
                    || await db.Projects.AnyAsync(p => p.ActiveDeploymentId == id, ct)
                )
                    return Results.Conflict(
                        new
                        {
                            error = "Only failed or cancelled deployment history can be deleted.",
                        }
                    );
                db.Deployments.Remove(deployment);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
        );
        api.MapPost(
            "/deployments/{id:guid}/rollback",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                await using var storageTransaction = await QueueTransactions.BeginAsync(db, ct);
                var source = await db.Deployments.FindAsync([id], ct);
                if (source is null)
                    return Results.NotFound();
                if (
                    source.State is not (DeploymentState.Running or DeploymentState.Stopped)
                    || source.ImageTag is null
                )
                    return Results.Conflict(
                        new { error = "Rollback requires an earlier successful retained image." }
                    );
                if (await QueueTransactions.HasExclusiveWorkAsync(db, source.ProjectId, ct))
                    return Results.Conflict(
                        new { error = "Wait for the pending project operation or backup." }
                    );
                var deployment = new Deployment
                {
                    ProjectId = source.ProjectId,
                    RollbackSourceId = source.Id,
                    ImageTag = source.ImageTag,
                    CommitSha = source.CommitSha,
                    CommitMessage = source.CommitMessage,
                    CommitAuthor = source.CommitAuthor,
                    ConfigurationJson = source.ConfigurationJson,
                    ProtectedComposeManifest = source.ProtectedComposeManifest,
                };
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);
                await storageTransaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/deployments/{deployment.Id}",
                    DeploymentResponse.From(deployment)
                );
            }
        );
        api.MapPost(
            "/projects/{id:guid}/restart",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
            {
                await using var storageTransaction = await QueueTransactions.BeginAsync(db, ct);
                var project = await db.Projects.FindAsync([id], ct);
                if (project?.ActiveDeploymentId is not { } active)
                    return Results.Conflict(
                        new { error = "Project has no retained active version to restart." }
                    );
                if (await QueueTransactions.HasExclusiveWorkAsync(db, id, ct))
                    return Results.Conflict(
                        new { error = "Wait for the pending project operation or backup." }
                    );
                var source = await db.Deployments.FindAsync([active], ct);
                if (source?.ImageTag is null)
                    return Results.Conflict(new { error = "Active image is unavailable." });
                var deployment = new Deployment
                {
                    ProjectId = id,
                    RollbackSourceId = source.Id,
                    ImageTag = source.ImageTag,
                    CommitSha = source.CommitSha,
                    CommitMessage = source.CommitMessage,
                    CommitAuthor = source.CommitAuthor,
                    ConfigurationJson = source.ConfigurationJson,
                    ProtectedComposeManifest = source.ProtectedComposeManifest,
                };
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);
                await storageTransaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/deployments/{deployment.Id}",
                    DeploymentResponse.From(deployment)
                );
            }
        );
    }
}
