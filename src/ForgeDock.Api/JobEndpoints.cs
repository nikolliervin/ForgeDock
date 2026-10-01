using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class JobEndpoints
{
    /// <summary>
    /// Registers validated task definitions, bounded secret-free execution history, and serialized manual
    /// queues. Pending runs retain history and block definition deletion.
    /// </summary>
    public static void MapJobEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects/{id:guid}/jobs",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                !await db.Projects.AnyAsync(p => p.Id == id, ct)
                    ? Results.NotFound()
                    : Results.Ok(
                        new
                        {
                            jobs = await db
                                .ScheduledJobs.AsNoTracking()
                                .Where(j => j.ProjectId == id)
                                .OrderBy(j => j.Name)
                                .ToListAsync(ct),
                            runs = await db
                                .JobRuns.AsNoTracking()
                                .Where(j => j.ProjectId == id)
                                .OrderByDescending(j => j.CreatedAt)
                                .Take(100)
                                .Select(j => new
                                {
                                    j.Id,
                                    j.Name,
                                    j.State,
                                    j.Output,
                                    j.ExitCode,
                                    j.Truncated,
                                    j.CreatedAt,
                                    j.FinishedAt,
                                    j.SourceDeploymentId,
                                    j.ScheduledJobId,
                                })
                                .ToListAsync(ct),
                        }
                    )
        );
        api.MapPost(
            "/projects/{id:guid}/jobs",
            async (Guid id, JobRequest request, ForgeDockDbContext db, CancellationToken ct) =>
            {
                if (
                    !JobScheduling.Valid(
                        request.Name,
                        request.Command,
                        request.IntervalMinutes,
                        request.TimeoutSeconds
                    )
                )
                    return Results.Problem(
                        "Use a name up to 80 characters, a nonempty command up to 4096 characters, a 0–10080 minute interval, and 1–900 second timeout.",
                        statusCode: 400
                    );
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var job = new ScheduledJob { ProjectId = id };
                Apply(job, request);
                db.ScheduledJobs.Add(job);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/projects/{id}/jobs", new { job.Id });
            }
        );
        api.MapPut(
            "/projects/{id:guid}/jobs/{jobId:guid}",
            async (
                Guid id,
                Guid jobId,
                JobRequest request,
                ForgeDockDbContext db,
                CancellationToken ct
            ) =>
            {
                if (
                    !JobScheduling.Valid(
                        request.Name,
                        request.Command,
                        request.IntervalMinutes,
                        request.TimeoutSeconds
                    )
                )
                    return Results.Problem("Invalid job settings.", statusCode: 400);
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                var job = await db.ScheduledJobs.SingleOrDefaultAsync(
                    j => j.ProjectId == id && j.Id == jobId,
                    ct
                );
                if (job is null)
                    return Results.NotFound();
                Apply(job, request);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
        api.MapDelete(
            "/projects/{id:guid}/jobs/{jobId:guid}",
            async (Guid id, Guid jobId, ForgeDockDbContext db, CancellationToken ct) =>
            {
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                var job = await db.ScheduledJobs.SingleOrDefaultAsync(
                    j => j.ProjectId == id && j.Id == jobId,
                    ct
                );
                if (job is null)
                    return Results.NotFound();
                if (
                    await db.JobRuns.AnyAsync(
                        j =>
                            j.ScheduledJobId == jobId
                            && (j.State == "Queued" || j.State == "Running"),
                        ct
                    )
                )
                    return Results.Conflict(
                        new { error = "Wait for this job's pending run before deleting it." }
                    );
                db.ScheduledJobs.Remove(job);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
        api.MapPost(
            "/projects/{id:guid}/jobs/{jobId:guid}/run",
            async (
                Guid id,
                Guid jobId,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                var job = await db.ScheduledJobs.SingleOrDefaultAsync(
                    j => j.ProjectId == id && j.Id == jobId,
                    ct
                );
                var project = await db.Projects.FindAsync([id], ct);
                if (job is null || project is null)
                    return Results.NotFound();
                if (await QueueTransactions.HasExclusiveWorkAsync(db, id, ct))
                    return Results.Conflict(
                        new { error = "Wait for the pending project operation or backup." }
                    );
                if (
                    await db.JobRuns.AnyAsync(
                        j =>
                            j.ScheduledJobId == jobId
                            && (j.State == "Queued" || j.State == "Running"),
                        ct
                    )
                )
                    return Results.Conflict(new { error = "This job already has a pending run." });
                var source = await db.Deployments.SingleOrDefaultAsync(
                    d => d.Id == project.ActiveDeploymentId && d.ProjectId == id,
                    ct
                );
                if (source is null)
                    return Results.Conflict(new { error = "Deploy an application first." });
                JobRun run;
                try
                {
                    run = JobScheduling.CreateRun(job, source, protector);
                }
                catch (InvalidOperationException error)
                {
                    return Results.Conflict(new { error = error.Message });
                }
                db.JobRuns.Add(run);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Accepted($"/api/projects/{id}/jobs", new { run.Id, run.State });
            }
        );
    }

    /// <summary>
    /// Updates the definition and recalculates its next due time; editing does not mutate snapshots of
    /// existing runs.
    /// </summary>
    private static void Apply(ScheduledJob job, JobRequest request)
    {
        job.Name = request.Name.Trim();
        job.Command = request.Command;
        job.IntervalMinutes = request.IntervalMinutes;
        job.TimeoutSeconds = request.TimeoutSeconds;
        job.Enabled = request.Enabled;
        job.NextRunAt =
            job.Enabled && job.IntervalMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(job.IntervalMinutes)
                : null;
        job.LastError = null;
    }
}

public sealed record JobRequest(
    string Name,
    string Command,
    int IntervalMinutes,
    int TimeoutSeconds,
    bool Enabled = true
);
