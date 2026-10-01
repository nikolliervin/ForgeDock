using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class ProjectEndpoints
{
    /// <summary>
    /// Registers validated project CRUD and durable stop/delete commands. Destructive commands check all
    /// pending resource consumers under the shared queue transaction.
    /// </summary>
    public static void MapProjectEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects",
            async (ForgeDockDbContext db, CancellationToken ct) =>
                await db
                    .Projects.AsNoTracking()
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => new ProjectResponse(
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
                    ))
                    .ToListAsync(ct)
        );
        api.MapPost(
            "/projects",
            async (
                ProjectRequest request,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                var errors = request.Validate();
                if (errors.Count > 0)
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["configuration"] = errors.ToArray() }
                    );
                var project = new Project
                {
                    Name = request.Name,
                    RepositoryUrl = request.RepositoryUrl,
                    Branch = request.Branch,
                    DeploymentMode = request.DeploymentMode,
                    ComposeFile = request.ComposeFile,
                    ComposeService = request.ComposeService,
                    BuildCommand = request.BuildCommand,
                    StartCommand = request.StartCommand,
                    RootDirectory = request.RootDirectory,
                    Dockerfile = request.Dockerfile,
                    ContainerPort = request.ContainerPort,
                    HealthPath = request.HealthPath,
                };
                db.Projects.Add(project);
                if (request.Database is { } kind)
                {
                    var password = DatabaseRuntime.NewPassword();
                    var service = new DatabaseService
                    {
                        ProjectId = project.Id,
                        Kind = kind,
                        ProtectedPassword = protector.Protect(password),
                    };
                    db.DatabaseServices.Add(service);
                    db.EnvironmentVariables.Add(
                        new ProjectEnvironment
                        {
                            ProjectId = project.Id,
                            Name = DatabaseRuntime.Variable(kind),
                            ProtectedValue = protector.Protect(
                                DatabaseRuntime.Connection(service, password)
                            ),
                        }
                    );
                }
                await db.SaveChangesAsync(ct);
                return Results.Created(
                    $"/api/projects/{project.Id}",
                    ProjectResponse.From(project)
                );
            }
        );
        api.MapGet(
            "/projects/{id:guid}",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct)
                    is { } project
                    ? Results.Ok(ProjectResponse.From(project))
                    : Results.NotFound()
        );
        api.MapPut(
            "/projects/{id:guid}",
            async (Guid id, ProjectRequest request, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var errors = request.Validate();
                if (errors.Count > 0)
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["configuration"] = errors.ToArray() }
                    );
                var project = await db.Projects.FindAsync([id], ct);
                if (project is null)
                    return Results.NotFound();
                project.Name = request.Name;
                project.RepositoryUrl = request.RepositoryUrl;
                project.Branch = request.Branch;
                project.DeploymentMode = request.DeploymentMode;
                project.ComposeFile = request.ComposeFile;
                project.ComposeService = request.ComposeService;
                project.BuildCommand = request.BuildCommand;
                project.StartCommand = request.StartCommand;
                project.RootDirectory = request.RootDirectory;
                project.Dockerfile = request.Dockerfile;
                project.ContainerPort = request.ContainerPort;
                project.HealthPath = request.HealthPath;
                await db.SaveChangesAsync(ct);
                return Results.Ok(ProjectResponse.From(project));
            }
        );
        api.MapPost(
            "/projects/{id:guid}/operations",
            async (
                Guid id,
                OperationRequest request,
                ForgeDockDbContext db,
                CancellationToken ct
            ) =>
            {
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                if (!Enum.IsDefined(request.Kind))
                    return Results.Problem("Unsupported project operation.", statusCode: 400);
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                if (await QueueTransactions.HasPendingWorkAsync(db, id, ct))
                    return Results.Conflict(
                        new
                        {
                            error = "Wait for pending deployments, jobs, backups, or project operations before stopping or deleting the project.",
                        }
                    );
                var operation = new ProjectOperation { ProjectId = id, Kind = request.Kind };
                db.Operations.Add(operation);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/operations/{operation.Id}",
                    new { operation.Id, operation.State }
                );
            }
        );
        api.MapGet(
            "/operations/{id:guid}",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                await db
                    .Operations.AsNoTracking()
                    .Where(o => o.Id == id)
                    .Select(o => new
                    {
                        o.Id,
                        o.ProjectId,
                        o.Kind,
                        o.State,
                        o.Error,
                    })
                    .SingleOrDefaultAsync(ct)
                    is { } operation
                    ? Results.Ok(operation)
                    : Results.NotFound()
        );
    }
}
