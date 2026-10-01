using System.Text.Json.Nodes;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public static class StorageRetention
{
    public static bool IsPending(Deployment deployment) =>
        deployment.State
            is not (
                DeploymentState.Running
                or DeploymentState.Stopped
                or DeploymentState.Failed
                or DeploymentState.Cancelled
            );

    /// <summary>
    /// Protects active/latest successful releases, pending builds/replays, and previous images during
    /// automatic rollback observation.
    /// </summary>
    public static HashSet<Guid> ProtectedDeployments(
        IEnumerable<Project> projects,
        IEnumerable<Deployment> deployments,
        int retained
    )
    {
        var all = deployments.ToList();
        var result = projects
            .Where(p => p.ActiveDeploymentId.HasValue)
            .Select(p => p.ActiveDeploymentId!.Value)
            .ToHashSet();
        foreach (var group in all.GroupBy(d => d.ProjectId))
        foreach (
            var deployment in group
                .Where(d =>
                    d.State is DeploymentState.Running or DeploymentState.Stopped
                    && d.ImageTag != null
                )
                .OrderByDescending(d => d.CreatedAt)
                .Take(retained)
        )
            result.Add(deployment.Id);
        foreach (var deployment in all.Where(IsPending))
        {
            result.Add(deployment.Id);
            if (deployment.RollbackSourceId.HasValue)
                result.Add(deployment.RollbackSourceId.Value);
        }
        foreach (
            var deployment in all.Where(d =>
                d.RollbackDeadlineAt > DateTimeOffset.UtcNow
                && d.AutoRollbackTriggeredAt == null
                && d.PreviousDeploymentId != null
            )
        )
            result.Add(deployment.PreviousDeploymentId!.Value);
        return result;
    }

    /// <summary>
    /// Includes all retained Compose service image tags, not just the routed application image.
    /// </summary>
    public static IEnumerable<string> Images(Deployment deployment, SecretProtector protector)
    {
        if (deployment.ImageTag is { } image)
            yield return image;
        if (deployment.ProtectedComposeManifest is { } manifest)
            foreach (
                var (_, service) in JsonNode.Parse(protector.Unprotect(manifest))![
                    "services"
                ]!.AsObject()
            )
                if (service?["image"]?.GetValue<string>() is { } tag)
                    yield return tag;
    }

    /// <summary>
    /// Recognizes only the ForgeDock GUID project/deployment tag format before cleanup can consider an
    /// image.
    /// </summary>
    public static bool ParseImage(string image, out Guid project, out Guid deployment)
    {
        project = deployment = Guid.Empty;
        var split = image.Split(':');
        if (split.Length != 2 || !Guid.TryParseExact(split[1], "N", out deployment))
            return false;
        var segments = split[0].Split('/');
        return segments.Length is 2 or 3
            && segments[0] == "forgedock"
            && Guid.TryParseExact(segments[1], "N", out project);
    }

    /// <summary>
    /// Measures regular files recursively without following symbolic links; inaccessible paths fail the
    /// preview instead of silently undercounting.
    /// </summary>
    public static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget != null)
            return 0;
        return Directory
            .EnumerateFiles(
                path,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false,
                }
            )
            .Sum(file => new FileInfo(file).Length);
    }
}

public sealed record StorageArtifact(string Kind, string Name, long SizeBytes, string Reason);

public sealed record StoragePreview(
    long RuntimeBytes,
    long FreeBytes,
    long TotalBytes,
    long ExpiredLogCount,
    IReadOnlyList<StorageArtifact> Artifacts
);
