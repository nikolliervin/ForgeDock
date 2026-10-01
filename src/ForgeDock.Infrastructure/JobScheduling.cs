using System.Text.Json.Nodes;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public static class JobScheduling
{
    public static bool Valid(string? name, string? command, int interval, int timeout) =>
        name?.Trim().Length is >= 1 and <= 80
        && !string.IsNullOrWhiteSpace(command)
        && TimedContainerCommand.Valid(command, timeout)
        && interval is >= 0 and <= 10080;

    /// <summary>
    /// Copies the active retained image, task definition, and release runtime environment into a durable
    /// immutable run. Compose tasks inherit only the routed service environment, not other service
    /// configuration.
    /// </summary>
    public static JobRun CreateRun(ScheduledJob job, Deployment source, SecretProtector protector)
    {
        if (
            source.ProjectId != job.ProjectId
            || source.ImageTag is null
            || source.State is not (DeploymentState.Running or DeploymentState.Stopped)
        )
            throw new InvalidOperationException("A retained active application image is required.");
        var snapshot = DeploymentSnapshot.Deserialize(source.ConfigurationJson);
        if (snapshot.DeploymentMode == DeploymentMode.Compose)
        {
            var model = JsonNode.Parse(
                protector.Unprotect(
                    source.ProtectedComposeManifest
                        ?? throw new InvalidOperationException("Missing Compose runtime manifest.")
                )
            )!;
            var environment = model["services"]!
                [snapshot.ComposeService]
                ?["environment"]?.AsObject();
            snapshot = snapshot with
            {
                ProtectedEnvironment =
                    environment
                        ?.Where(e => e.Value != null)
                        .ToDictionary(
                            e => e.Key,
                            e => protector.Protect(e.Value!.GetValue<string>())
                        ) ?? [],
            };
        }
        else if (
            snapshot.DeploymentMode == DeploymentMode.Auto
            && !snapshot.ProtectedEnvironment.ContainsKey("PORT")
        )
            snapshot.ProtectedEnvironment["PORT"] = protector.Protect(
                snapshot.ContainerPort.ToString()
            );
        return new JobRun
        {
            ProjectId = job.ProjectId,
            ScheduledJobId = job.Id,
            SourceDeploymentId = source.Id,
            Name = job.Name,
            Command = job.Command,
            TimeoutSeconds = job.TimeoutSeconds,
            ImageTag = source.ImageTag,
            ConfigurationJson = snapshot.Serialize(),
        };
    }
}
