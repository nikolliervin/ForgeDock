using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public record DeploymentRequest(string? CommitSha = null);

public record EnvironmentRequest(string Value);

public record OperationRequest(ProjectOperationKind Kind);

public record ProjectRequest(
    string Name,
    string RepositoryUrl,
    string Branch = "main",
    string Dockerfile = "Dockerfile",
    int ContainerPort = 8080,
    string HealthPath = "/",
    DeploymentMode DeploymentMode = DeploymentMode.Dockerfile,
    string ComposeFile = "docker-compose.yml",
    string ComposeService = "",
    string BuildCommand = "",
    string StartCommand = "",
    string RootDirectory = ".",
    DatabaseKind? Database = null,
    bool AcceptSqlServerLicense = false
)
{
    public IReadOnlyList<string> Validate() =>
        ProjectConfiguration
            .Validate(
                Name ?? "",
                RepositoryUrl ?? "",
                Branch ?? "",
                Dockerfile ?? "",
                ContainerPort,
                HealthPath ?? "",
                DeploymentMode,
                ComposeFile ?? "",
                ComposeService ?? "",
                BuildCommand,
                StartCommand,
                RootDirectory
            )
            .Concat(
                Database is { } kind && !Enum.IsDefined(kind)
                    ? new[] { "Unsupported database service." }
                    : Array.Empty<string>()
            )
            .Concat(
                Database == DatabaseKind.SqlServer && !AcceptSqlServerLicense
                    ? new[]
                    {
                        "Accept the SQL Server Express license before creating this service.",
                    }
                    : Array.Empty<string>()
            )
            .ToArray();
}

public record ProjectResponse(
    Guid Id,
    string Name,
    string RepositoryUrl,
    string Branch,
    string Dockerfile,
    int ContainerPort,
    string HealthPath,
    Guid? ActiveDeploymentId,
    string HealthStatus,
    DeploymentMode DeploymentMode,
    string ComposeFile,
    string ComposeService,
    string BuildCommand,
    string StartCommand,
    string RootDirectory
)
{
    public static ProjectResponse From(Project p) =>
        new(
            p.Id,
            p.Name,
            p.RepositoryUrl,
            p.Branch,
            p.Dockerfile,
            p.ContainerPort,
            p.HealthPath,
            p.ActiveDeploymentId,
            p.HealthStatus,
            p.DeploymentMode,
            p.ComposeFile,
            p.ComposeService,
            p.BuildCommand,
            p.StartCommand,
            p.RootDirectory
        );
}

public record DeploymentResponse(
    Guid Id,
    Guid ProjectId,
    DeploymentState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CommitSha,
    string? Error,
    Guid? RollbackSourceId,
    IReadOnlyList<ServiceStatus> Services,
    string? CommitMessage,
    string? CommitAuthor,
    DeploymentState LastStage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Trigger = "Manual",
    bool CanRollback = true
)
{
    public static DeploymentResponse From(Deployment d) =>
        new(
            d.Id,
            d.ProjectId,
            d.State,
            d.CreatedAt,
            d.UpdatedAt,
            d.CommitSha,
            d.Error,
            d.RollbackSourceId,
            string.IsNullOrWhiteSpace(d.ServiceStatusJson)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<ServiceStatus>>(
                    d.ServiceStatusJson
                )
                ?? [],
            d.CommitMessage,
            d.CommitAuthor,
            d.LastStage,
            d.StartedAt,
            d.FinishedAt,
            d.Trigger,
            d.ImageTag != null && d.State is DeploymentState.Running or DeploymentState.Stopped
        );
}
