namespace ForgeDock.Domain;

public enum DeploymentMode { Dockerfile, Compose, Auto }

public sealed class Project
{
    public bool AutoRollbackEnabled { get; set; }
    public int RollbackWindowMinutes { get; set; } = 10;
    public int RollbackFailureThreshold { get; set; } = 3;
    public string PreDeployCommand { get; set; } = "";
    public string PostDeployCommand { get; set; } = "";
    public int HookTimeoutSeconds { get; set; } = 120;
    public string? ApplicationName { get; set; }
    public string? EnvironmentName { get; set; }
    public double CpuLimit { get; set; } = 1;
    public int MemoryLimitMiB { get; set; } = 512;
    public bool ResourceAlertsEnabled { get; set; } = true;
    public bool PreviewsEnabled { get; set; }
    public Guid? ParentProjectId { get; set; }
    public int? PullRequestNumber { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.Dockerfile;
    public string ComposeFile { get; set; } = "docker-compose.yml";
    public string ComposeService { get; set; } = "";
    public string BuildCommand { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public string RootDirectory { get; set; } = ".";
    public string Dockerfile { get; set; } = "Dockerfile";
    public int ContainerPort { get; set; } = 8080;
    public string HealthPath { get; set; } = "/";
    public string HealthStatus { get; set; } = "NotDeployed";
    public Guid? ActiveDeploymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
