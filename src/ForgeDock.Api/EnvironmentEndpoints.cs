using ForgeDock.Application;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class EnvironmentEndpoints
{
    /// <summary>
    /// Registers atomic validated .env imports with explicit replacement consent. Parsing finishes before
    /// persistence and returned data includes names only.
    /// </summary>
    public static void MapEnvironmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut(
            "/projects/{id:guid}/environment",
            async (
                Guid id,
                EnvironmentFileRequest request,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                Dictionary<string, string> values;
                try
                {
                    values = EnvironmentFile.Parse(request.Content);
                }
                catch (FormatException error)
                {
                    return Results.Problem(error.Message, statusCode: 400);
                }
                await using var transaction = await QueueTransactions.BeginAsync(db, ct);
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var names = values.Keys.ToArray();
                var existing = await db
                    .EnvironmentVariables.Where(v => v.ProjectId == id && names.Contains(v.Name))
                    .ToDictionaryAsync(v => v.Name, ct);
                if (!request.Overwrite && existing.Count > 0)
                    return Results.Conflict(
                        new
                        {
                            error = "Some variables already exist. Enable replacement to update them.",
                            names = existing.Keys.Order().ToArray(),
                        }
                    );
                foreach (var (name, value) in values)
                {
                    if (!existing.TryGetValue(name, out var variable))
                    {
                        variable = new ProjectEnvironment { ProjectId = id, Name = name };
                        db.EnvironmentVariables.Add(variable);
                    }
                    variable.ProtectedValue = protector.Protect(value);
                }
                // One SaveChanges transaction writes the entire validated batch.
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(new { saved = values.Count, names = names.Order().ToArray() });
            }
        );
    }
}

public record EnvironmentFileRequest(string Content, bool Overwrite = false);
