using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class ProjectEnvironmentEndpoints
{
    /// <summary>
    /// Registers name-only environment reads and encrypted single-variable writes. Queue locking coordinates
    /// writes with bulk imports; plaintext values never appear in responses.
    /// </summary>
    public static void MapProjectEnvironmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects/{id:guid}/environment",
            async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
                await db
                    .EnvironmentVariables.Where(e => e.ProjectId == id)
                    .Select(e => e.Name)
                    .OrderBy(n => n)
                    .ToListAsync(ct)
        );
        api.MapPut(
            "/projects/{id:guid}/environment/{name}",
            async (
                Guid id,
                string name,
                EnvironmentRequest request,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                if (
                    !System.Text.RegularExpressions.Regex.IsMatch(
                        name,
                        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$"
                    )
                    || request.Value is null
                    || request.Value.Length > 16384
                    || request.Value.Any(c => c is '\r' or '\n' or '\0')
                )
                    return Results.Problem(
                        "Use a valid variable name and a single-line value up to 16 KiB.",
                        statusCode: 400
                    );
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var variable = await db.EnvironmentVariables.FindAsync([id, name], ct);
                if (variable is null)
                {
                    variable = new ProjectEnvironment { ProjectId = id, Name = name };
                    db.EnvironmentVariables.Add(variable);
                }
                variable.ProtectedValue = protector.Protect(request.Value);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
        api.MapDelete(
            "/projects/{id:guid}/environment/{name}",
            async (Guid id, string name, ForgeDockDbContext db, CancellationToken ct) =>
            {
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                var variable = await db.EnvironmentVariables.FindAsync([id, name], ct);
                if (variable is null)
                    return Results.NotFound();
                db.EnvironmentVariables.Remove(variable);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
    }
}
