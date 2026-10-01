using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed partial class Worker
{
    /// <summary>
    /// Coalesces due intervals into one pending run per task, then executes the oldest queued run in its own
    /// constrained container. Init enables timeout signals; docker wait supplies the final exit status
    /// rather than a transient inspect value.
    /// </summary>
    private async Task ProcessScheduledJobs(ForgeDockDbContext db, CancellationToken ct)
    {
        await using (var transaction = await QueueTransactions.BeginAsync(db, ct))
        {
            foreach (
                var job in await db
                    .ScheduledJobs.Where(j =>
                        j.Enabled && j.IntervalMinutes > 0 && j.NextRunAt <= DateTimeOffset.UtcNow
                    )
                    .ToListAsync(ct)
            )
            {
                if (await QueueTransactions.HasExclusiveWorkAsync(db, job.ProjectId, ct))
                    continue;
                if (
                    await db.JobRuns.AnyAsync(
                        r =>
                            r.ScheduledJobId == job.Id
                            && (r.State == "Queued" || r.State == "Running"),
                        ct
                    )
                )
                    continue;
                job.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(job.IntervalMinutes);
                var project = await db.Projects.FindAsync([job.ProjectId], ct);
                var source = project?.ActiveDeploymentId is { } active
                    ? await db.Deployments.FindAsync([active], ct)
                    : null;
                try
                {
                    if (source is null)
                        throw new InvalidOperationException();
                    db.JobRuns.Add(JobScheduling.CreateRun(job, source, protector));
                    job.LastError = null;
                }
                catch (InvalidOperationException)
                {
                    job.LastError =
                        "Scheduled run skipped: deploy a retained application image first.";
                }
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        var run = await db
            .JobRuns.Where(r => r.State == "Queued")
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null)
            return;
        var snapshot = DeploymentSnapshot.Deserialize(run.ConfigurationJson);
        run.State = "Running";
        await db.SaveChangesAsync(ct);
        var directory = Path.Combine(
            Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime"),
            "secrets"
        );
        Directory.CreateDirectory(directory);
        var envFile = Path.Combine(directory, $"job-{run.Id:N}.env");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds + 15));
            using (var stream = PrivateFiles.Create(envFile))
            using (var writer = new StreamWriter(stream))
                foreach (var (name, value) in snapshot.ProtectedEnvironment)
                {
                    var plain = protector.Unprotect(value);
                    if (plain.Contains('\n') || plain.Contains('\r'))
                        throw new InvalidOperationException(
                            "Task environment values must be single-line."
                        );
                    await writer.WriteLineAsync($"{name}={plain}");
                }
            run.ContainerId = await runner.RunAsync(
                "docker",
                [
                    "create",
                    "--name",
                    $"forgedock-job-{run.Id:N}",
                    "--network",
                    configuration["ForgeDock:Network"] ?? "forgedock",
                    "--label",
                    "io.forgedock.managed=true",
                    "--label",
                    $"io.forgedock.project={run.ProjectId}",
                    "--label",
                    $"io.forgedock.job={run.Id}",
                    "--init",
                    "--cap-drop",
                    "ALL",
                    "--security-opt",
                    "no-new-privileges:true",
                    "--pids-limit",
                    "256",
                    "--env-file",
                    envFile,
                    .. ResourceLimits.DockerArguments(snapshot.CpuLimit, snapshot.MemoryLimitMiB),
                    "--entrypoint",
                    "/bin/sh",
                    run.ImageTag,
                    "-c",
                    TimedContainerCommand.ShellCommand(run.Command, run.TimeoutSeconds),
                ],
                null,
                _ => Task.CompletedTask,
                timeout.Token,
                inheritEnvironment: false
            );
            await db.SaveChangesAsync(ct);
            await AttachDatabases(db, run.ProjectId, run.ContainerId, timeout.Token);
            var result = await BoundedDockerProcess.Run(
                ["start", "--attach", run.ContainerId],
                timeout.Token
            );
            run.Output = TimedContainerCommand.Redact(
                result.Output,
                snapshot.ProtectedEnvironment.Values.Select(protector.Unprotect),
                result.Truncated
            );
            run.Truncated = result.Truncated || run.Output.Length > 65536;
            if (run.Output.Length > 65536)
                run.Output = run.Output[..65536];
            run.ExitCode = int.Parse(
                await runner.RunAsync(
                    "docker",
                    ["wait", run.ContainerId],
                    null,
                    _ => Task.CompletedTask,
                    timeout.Token,
                    inheritEnvironment: false
                )
            );
            run.State = run.ExitCode == 0 ? "Completed" : "Failed";
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            run.State = "Failed";
            run.Output =
                error is OperationCanceledException
                    ? "Job exceeded its timeout; its container is being removed."
                    : "Job execution failed. Check the image, shell/timeout utilities, and database connectivity.";
        }
        finally
        {
            File.Delete(envFile);
            await RemoveJobContainer(run, CancellationToken.None);
        }
        run.FinishedAt = DateTimeOffset.UtcNow;
        if (run.State == "Failed")
        {
            var settings = await db.NotificationSettings.FindAsync([run.ProjectId], ct);
            if (settings?.OnFailure == true)
                foreach (var channel in new[] { "Slack", "Discord", "Email" })
                    if (
                        channel == "Slack" ? settings.ProtectedSlackUrl.Length > 0
                        : channel == "Discord" ? settings.ProtectedDiscordUrl.Length > 0
                        : settings.Email.Length > 0
                    )
                        db.NotificationDeliveries.Add(
                            new NotificationDelivery
                            {
                                ProjectId = run.ProjectId,
                                DeploymentId = run.SourceDeploymentId,
                                Event = "JobFailed:" + run.Id,
                                Channel = channel,
                                Message =
                                    $"Job '{run.Name}' failed. "
                                    + NotificationPolicy
                                        .LogLink(
                                            configuration["ForgeDock:DashboardBaseUrl"]
                                                ?? "http://localhost:5173",
                                            run.ProjectId,
                                            run.SourceDeploymentId
                                        )
                                        .Replace(
                                            $"/deployments?deployment={run.SourceDeploymentId}",
                                            $"/jobs?run={run.Id}"
                                        ),
                            }
                        );
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes only the run-owned task container; the deterministic name recovers a crash between Docker
    /// creation and ID persistence.
    /// </summary>
    private async Task RemoveJobContainer(JobRun run, CancellationToken ct)
    {
        try
        {
            // The deterministic name also recovers a crash between container creation and ID persistence.
            var id = run.ContainerId ?? $"forgedock-job-{run.Id:N}";
            var label = await runner.RunAsync(
                "docker",
                ["inspect", "--format", "{{index .Config.Labels \"io.forgedock.job\"}}", id],
                null,
                _ => Task.CompletedTask,
                ct,
                inheritEnvironment: false
            );
            if (label == run.Id.ToString())
                await runner.RunAsync(
                    "docker",
                    ["rm", "-f", "-v", id],
                    null,
                    _ => Task.CompletedTask,
                    ct,
                    inheritEnvironment: false
                );
        }
        catch
        {
            logger.LogWarning(
                "Could not remove task container for job run {Id}; inspect managed job containers.",
                run.Id
            );
        }
    }
}
