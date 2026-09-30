namespace ForgeDock.Domain;

public enum DeploymentMode { Dockerfile, Compose, Auto }

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.Dockerfile;
    public string ComposeFile { get; set; } = "docker-compose.yml";
    public string ComposeService { get; set; } = "";
    public string BuildCommand { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public string Dockerfile { get; set; } = "Dockerfile";
    public int ContainerPort { get; set; } = 8080;
    public string HealthPath { get; set; } = "/";
    public string HealthStatus { get; set; } = "NotDeployed";
    public Guid? ActiveDeploymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
