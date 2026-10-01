using System.Text.Json;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record DeploymentSnapshot(
    string RepositoryUrl,
    string Branch,
    string Dockerfile,
    int ContainerPort,
    string HealthPath,
    Dictionary<string, string> ProtectedEnvironment,
    DeploymentMode DeploymentMode = DeploymentMode.Dockerfile,
    string ComposeFile = "docker-compose.yml",
    string ComposeService = "",
    string BuildCommand = "",
    string StartCommand = "",
    string RootDirectory = ".",
    double CpuLimit = 1,
    int MemoryLimitMiB = 512,
    string PreDeployCommand = "",
    string PostDeployCommand = "",
    int HookTimeoutSeconds = 120,
    bool AutoRollbackEnabled = false,
    int RollbackWindowMinutes = 10,
    int RollbackFailureThreshold = 3
)
{
    /// <summary>
    /// Copies project configuration and already-encrypted environment values into a queued release so later
    /// edits do not change its behavior.
    /// </summary>
    public static DeploymentSnapshot Create(
        Project p,
        IEnumerable<ProjectEnvironment> environment
    ) =>
        new(
            p.RepositoryUrl,
            p.Branch,
            p.Dockerfile,
            p.ContainerPort,
            p.HealthPath,
            environment.ToDictionary(e => e.Name, e => e.ProtectedValue),
            p.DeploymentMode,
            p.ComposeFile,
            p.ComposeService,
            p.BuildCommand,
            p.StartCommand,
            p.RootDirectory,
            p.CpuLimit,
            p.MemoryLimitMiB,
            p.PreDeployCommand,
            p.PostDeployCommand,
            p.HookTimeoutSeconds,
            p.AutoRollbackEnabled,
            p.RollbackWindowMinutes,
            p.RollbackFailureThreshold
        );

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Restores the version-tolerant release snapshot; optional constructor parameters preserve defaults for
    /// older persisted JSON.
    /// </summary>
    public static DeploymentSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<DeploymentSnapshot>(json)
        ?? throw new InvalidOperationException("Deployment configuration snapshot is missing.");
}

public sealed class ProjectEnvironment
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
}
