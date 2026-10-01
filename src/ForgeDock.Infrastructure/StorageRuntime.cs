using System.Text.Json.Nodes;
using ForgeDock.Domain;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Infrastructure;

public sealed class StorageRuntime(
    ProcessRunner runner,
    SecretProtector protector,
    string runtimePath,
    Func<string[], CancellationToken, Task<string>>? docker = null
)
{
    public const int LockId = QueueTransactions.LockId;
    private readonly string root = Path.GetFullPath(runtimePath);

    private Task<string> Docker(string[] args, CancellationToken ct) =>
        docker is not null
            ? docker(args, ct)
            : runner.RunAsync(
                "docker",
                args,
                null,
                _ => Task.CompletedTask,
                ct,
                inheritEnvironment: false
            );

    /// <summary>
    /// Calculates eligibility from persisted references and exact owned image tags without deleting
    /// resources. Shared image layers mean sizes are not guaranteed reclaimed bytes.
    /// </summary>
    public async Task<StoragePreview> Preview(
        ForgeDockDbContext db,
        StoragePolicy policy,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
        var deployments = await db.Deployments.AsNoTracking().ToListAsync(ct);
        var protectedIds = StorageRetention.ProtectedDeployments(
            projects,
            deployments,
            policy.RetainedDeployments
        );
        var protectedImages = deployments
            .Where(d => protectedIds.Contains(d.Id))
            .SelectMany(d => StorageRetention.Images(d, protector))
            .ToHashSet();
        foreach (
            var image in await db
                .JobRuns.Where(j => j.State == "Queued" || j.State == "Running")
                .Select(j => j.ImageTag)
                .ToListAsync(ct)
        )
            protectedImages.Add(image);
        var pendingIds = deployments
            .Where(StorageRetention.IsPending)
            .Select(d => d.Id)
            .ToHashSet();
        var artifacts = new List<StorageArtifact>();
        var sources = Path.Combine(root, "sources");
        if (Directory.Exists(sources) && new DirectoryInfo(sources).LinkTarget is null)
            foreach (var directory in Directory.EnumerateDirectories(sources))
            {
                var info = new DirectoryInfo(directory);
                if (
                    info.LinkTarget is not null
                    || !Guid.TryParseExact(info.Name, "N", out var id)
                    || pendingIds.Contains(id)
                )
                    continue;
                var deployment = deployments.Find(d => d.Id == id);
                var time = deployment?.CreatedAt.UtcDateTime ?? info.LastWriteTimeUtc;
                if (
                    time
                    > now.AddDays(
                        -(
                            deployment is null
                                ? policy.OrphanRetentionDays
                                : policy.SourceRetentionDays
                        )
                    ).UtcDateTime
                )
                    continue;
                artifacts.Add(
                    new(
                        "Source",
                        info.Name,
                        StorageRetention.DirectoryBytes(directory),
                        deployment is null
                            ? "Orphaned deployment or preview checkout"
                            : "Source retention elapsed"
                    )
                );
            }
        var images = (await Docker(["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Distinct();
        foreach (var image in images)
        {
            if (
                !StorageRetention.ParseImage(image, out var project, out var id)
                || protectedImages.Contains(image)
                || protectedIds.Contains(id)
            )
                continue;
            var info = JsonNode.Parse(await Docker(["image", "inspect", image], ct))!.AsArray()[0]!;
            var labels = info["Config"]?["Labels"];
            if (
                labels?["io.forgedock.project"] is { } label
                && label.GetValue<string>() != project.ToString()
            )
                continue;
            if (
                !deployments.Any(d => d.Id == id)
                && DateTimeOffset.Parse(info["Created"]!.GetValue<string>())
                    > now.AddDays(-policy.OrphanRetentionDays)
            )
                continue;
            artifacts.Add(
                new(
                    "Image",
                    image,
                    info["Size"]!.GetValue<long>(),
                    "Outside retained deployments or orphan grace period"
                )
            );
        }
        var logCount = await db
            .Logs.Where(l =>
                l.Timestamp < now.AddDays(-policy.LogRetentionDays)
                && !pendingIds.Contains(l.DeploymentId)
            )
            .LongCountAsync(ct);
        var drive = new DriveInfo(root);
        return new(
            StorageRetention.DirectoryBytes(root),
            drive.AvailableFreeSpace,
            drive.TotalSize,
            logCount,
            artifacts
        );
    }

    /// <summary>
    /// Holds a session lock across plan recomputation and deletion so queued consumers cannot acquire images
    /// being removed. Commits cleared image references after each successful removal; filesystem/Docker
    /// deletion is not transactional. Never forces image removal or deletes volumes.
    /// </summary>
    public async Task<IReadOnlyList<StorageArtifact>> Cleanup(
        ForgeDockDbContext db,
        StoragePolicy policy,
        CancellationToken ct
    )
    {
        // API rollback/restart queues use this same transaction lock to protect retained images.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({LockId})", ct);
            var plan = await Preview(db, policy, ct);
            var removed = new List<StorageArtifact>();
            foreach (var artifact in plan.Artifacts)
            {
                if (artifact.Kind == "Source")
                {
                    var sources = new DirectoryInfo(Path.Combine(root, "sources"));
                    var path = Path.Combine(sources.FullName, artifact.Name);
                    if (
                        sources.LinkTarget is not null
                        || new DirectoryInfo(path).LinkTarget is not null
                    )
                        continue;
                    Directory.Delete(path, true);
                    removed.Add(artifact);
                }
                else
                {
                    // Never force image removal. Docker refuses deletion while any container uses it.
                    // Remove only stopped, owned app containers for this exact image first.
                    var containers = (
                        await Docker(
                            [
                                "ps",
                                "-a",
                                "--filter",
                                "ancestor=" + artifact.Name,
                                "--format",
                                "{{.ID}}",
                            ],
                            ct
                        )
                    ).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    StorageRetention.ParseImage(artifact.Name, out var project, out _);
                    var active = await db
                        .Projects.Where(p => p.ActiveDeploymentId != null)
                        .Select(p => p.ActiveDeploymentId!.Value.ToString())
                        .ToListAsync(ct);
                    foreach (var container in containers)
                    {
                        var info = JsonNode
                            .Parse(await Docker(["inspect", container], ct))!
                            .AsArray()[0]!;
                        var labels = info["Config"]?["Labels"];
                        if (
                            info["State"]?["Running"]?.GetValue<bool>() != false
                            || labels?["io.forgedock.managed"]?.GetValue<string>() != "true"
                            || labels?["io.forgedock.project"]?.GetValue<string>()
                                != project.ToString()
                            || labels?["io.forgedock.database"] is not null
                        )
                            continue;
                        if (
                            labels?["io.forgedock.deployment"]?.GetValue<string>() is { } deployment
                            && !active.Contains(deployment)
                        )
                            await Docker(["rm", container], ct);
                        else if (!await db.Projects.AnyAsync(p => p.Id == project, ct))
                            await Docker(["rm", container], ct);
                    }
                    try
                    {
                        await Docker(["image", "rm", artifact.Name], ct);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    } // In-use/shared images remain; next preview reports them.
                    removed.Add(artifact);
                    foreach (
                        var deployment in await db
                            .Deployments.Where(d => d.ImageTag == artifact.Name)
                            .ToListAsync(ct)
                    )
                        deployment.ImageTag = null;
                    await db.SaveChangesAsync(ct);
                }
            }
            var pending = await db
                .Deployments.Where(d =>
                    d.State != DeploymentState.Running
                    && d.State != DeploymentState.Stopped
                    && d.State != DeploymentState.Failed
                    && d.State != DeploymentState.Cancelled
                )
                .Select(d => d.Id)
                .ToListAsync(ct);
            await db
                .Logs.Where(l =>
                    l.Timestamp < DateTimeOffset.UtcNow.AddDays(-policy.LogRetentionDays)
                    && !pending.Contains(l.DeploymentId)
                )
                .ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            return removed;
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_unlock({LockId})",
                    CancellationToken.None
                );
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
