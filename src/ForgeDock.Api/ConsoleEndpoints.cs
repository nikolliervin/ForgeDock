using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class ConsoleEndpoints
{
    /// <summary>
    /// Registers authenticated, time-bounded execution against the verified active container. Access logs
    /// omit command text and output because both may contain secrets.
    /// </summary>
    public static void MapConsoleEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(
            "/projects/{id:guid}/console",
            async (
                Guid id,
                ConsoleRequest request,
                ForgeDockDbContext db,
                ILoggerFactory loggerFactory,
                CancellationToken ct
            ) =>
            {
                if (!ContainerConsole.ValidCommand(request.Command))
                    return Results.Problem(
                        "Enter a command of up to 4096 characters without null bytes.",
                        statusCode: 400
                    );
                var project = await db
                    .Projects.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == id, ct);
                if (project is null)
                    return Results.NotFound();
                if (
                    project.ActiveDeploymentId is not { } active
                    || project.HealthStatus == "Stopped"
                )
                    return Results.Conflict(
                        new { error = "Deploy and start an application before using the console." }
                    );
                var deployment = await db
                    .Deployments.AsNoTracking()
                    .SingleOrDefaultAsync(d => d.Id == active && d.ProjectId == id, ct);
                if (
                    deployment is null
                    || deployment.State != DeploymentState.Running
                    || deployment.ContainerId is not { } name
                )
                    return Results.Conflict(
                        new { error = "The active application container is unavailable." }
                    );
                if (
                    await db.Operations.AnyAsync(
                        o =>
                            o.ProjectId == id
                            && (
                                o.State == ProjectOperationState.Queued
                                || o.State == ProjectOperationState.Running
                            ),
                        ct
                    )
                    || await db.Deployments.AnyAsync(
                        d =>
                            d.ProjectId == id
                            && d.State != DeploymentState.Running
                            && d.State != DeploymentState.Failed
                            && d.State != DeploymentState.Cancelled
                            && d.State != DeploymentState.Stopped,
                        ct
                    )
                )
                    return Results.Conflict(
                        new { error = "Wait for the deployment or project operation to finish." }
                    );
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var inspection = await new ProcessRunner().RunAsync(
                        "docker",
                        ["inspect", "--", name],
                        null,
                        _ => Task.CompletedTask,
                        timeout.Token,
                        inheritEnvironment: false
                    );
                    var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
                    var containerId = ContainerConsole.VerifyContainer(
                        inspection,
                        id,
                        active,
                        snapshot.DeploymentMode == DeploymentMode.Compose,
                        deployment.ImageTag
                    );
                    // Record access without storing commands or output, which can contain application secrets.
                    loggerFactory
                        .CreateLogger("ProjectConsole")
                        .LogInformation(
                            "Console execution for project {ProjectId}, deployment {DeploymentId}",
                            id,
                            active
                        );
                    return Results.Ok(
                        await ContainerConsole.ExecuteAsync(
                            containerId,
                            request.Command!,
                            timeout.Token
                        )
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem(
                        "Console request exceeded 30 seconds. The command may still be running inside the container.",
                        statusCode: 408
                    );
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return Results.Problem(
                        "Docker is unavailable on the API host. Install the Docker CLI and configure daemon access.",
                        statusCode: 503
                    );
                }
                catch (InvalidOperationException error)
                {
                    return Results.Conflict(new { error = error.Message });
                }
            }
        );
    }
}

public sealed record ConsoleRequest(string? Command);
