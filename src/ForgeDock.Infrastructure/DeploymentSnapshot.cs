using ForgeDock.Domain;
using System.Text.Json;

namespace ForgeDock.Infrastructure;

public sealed record DeploymentSnapshot(string RepositoryUrl, string Branch, string Dockerfile,
    int ContainerPort, string HealthPath, Dictionary<string, string> ProtectedEnvironment, DeploymentMode DeploymentMode = DeploymentMode.Dockerfile,
    string ComposeFile = "docker-compose.yml", string ComposeService = "", string BuildCommand = "", string StartCommand = "", string RootDirectory = ".", double CpuLimit = 1, int MemoryLimitMiB = 512)
{
    public static DeploymentSnapshot Create(Project p, IEnumerable<ProjectEnvironment> environment) =>
        new(p.RepositoryUrl, p.Branch, p.Dockerfile, p.ContainerPort, p.HealthPath,
            environment.ToDictionary(e => e.Name, e => e.ProtectedValue), p.DeploymentMode, p.ComposeFile, p.ComposeService, p.BuildCommand, p.StartCommand, p.RootDirectory, p.CpuLimit, p.MemoryLimitMiB);
    public string Serialize() => JsonSerializer.Serialize(this);
    public static DeploymentSnapshot Deserialize(string json) => JsonSerializer.Deserialize<DeploymentSnapshot>(json)
        ?? throw new InvalidOperationException("Deployment configuration snapshot is missing.");
}

public sealed class ProjectEnvironment
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
}
