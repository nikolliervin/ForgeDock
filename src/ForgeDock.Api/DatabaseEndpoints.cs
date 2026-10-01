using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class DatabaseEndpoints
{
    /// <summary>
    /// Registers database provisioning requests and secret-free status DTOs. A unique project/engine
    /// constraint and serialized creation prevent duplicate services.
    /// </summary>
    public static void MapDatabaseEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects/{id:guid}/databases",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                !await db.Projects.AnyAsync(p => p.Id == id, ct)
                    ? Results.NotFound()
                    : Results.Ok(
                        await db
                            .DatabaseServices.AsNoTracking()
                            .Where(s => s.ProjectId == id)
                            .Select(s => new
                            {
                                s.Id,
                                s.Kind,
                                s.State,
                                s.Error,
                                s.CreatedAt,
                                s.BackupIntervalHours,
                                s.NextBackupAt,
                            })
                            .ToListAsync(ct)
                    )
        );
        api.MapPost(
            "/projects/{id:guid}/databases",
            async (
                Guid id,
                DatabaseRequest request,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                if (!Enum.IsDefined(request.Kind))
                    return Results.BadRequest();
                if (request.Kind == DatabaseKind.SqlServer && !request.AcceptSqlServerLicense)
                    return Results.Problem(
                        "Accept the SQL Server Express license before creating this service.",
                        statusCode: 400
                    );
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                if (
                    await db
                        .Projects.FromSqlInterpolated(
                            $"SELECT * FROM \"Projects\" WHERE \"Id\" = {id} FOR UPDATE"
                        )
                        .SingleOrDefaultAsync(ct)
                    is null
                )
                    return Results.NotFound();
                if (
                    await db.DatabaseServices.AnyAsync(
                        s => s.ProjectId == id && s.Kind == request.Kind,
                        ct
                    )
                )
                    return Results.Conflict(
                        new { error = "This database service already exists." }
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
                )
                    return Results.Conflict(new { error = "Wait for the project operation." });
                var variable = DatabaseRuntime.Variable(request.Kind);
                if (
                    await db.EnvironmentVariables.AnyAsync(
                        e => e.ProjectId == id && e.Name == variable,
                        ct
                    )
                )
                    return Results.Conflict(
                        new
                        {
                            error = $"Remove the existing {variable} variable before creating a managed database.",
                        }
                    );
                var password = DatabaseRuntime.NewPassword();
                var service = new DatabaseService
                {
                    ProjectId = id,
                    Kind = request.Kind,
                    ProtectedPassword = protector.Protect(password),
                };
                db.DatabaseServices.Add(service);
                db.EnvironmentVariables.Add(
                    new ProjectEnvironment
                    {
                        ProjectId = id,
                        Name = variable,
                        ProtectedValue = protector.Protect(
                            DatabaseRuntime.Connection(service, password)
                        ),
                    }
                );
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Accepted(
                    $"/api/projects/{id}/databases",
                    new
                    {
                        service.Id,
                        service.Kind,
                        service.State,
                        variable,
                    }
                );
            }
        );
        api.MapPost(
            "/projects/{id:guid}/databases/{serviceId:guid}/retry",
            async (Guid id, Guid serviceId, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var service = await db.DatabaseServices.SingleOrDefaultAsync(
                    s => s.ProjectId == id && s.Id == serviceId,
                    ct
                );
                if (service is null)
                    return Results.NotFound();
                if (service.State is not ("Failed" or "Stopped"))
                    return Results.Conflict(
                        new { error = "Only failed or stopped services can be retried." }
                    );
                service.State = "Queued";
                service.Error = null;
                await db.SaveChangesAsync(ct);
                return Results.Accepted();
            }
        );
    }
}

public sealed record DatabaseRequest(DatabaseKind Kind, bool AcceptSqlServerLicense = false);
