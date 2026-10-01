using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed partial class Worker
{
    private DatabaseRuntime Databases =>
        new(runner, protector, configuration["ForgeDock:RuntimePath"] ?? ".runtime");

    /// <summary>
    /// Processes requested database provisioning in the serial worker and records sanitized failure
    /// guidance.
    /// </summary>
    private async Task ProvisionDatabases(ForgeDockDbContext db, CancellationToken ct)
    {
        foreach (
            var service in await db
                .DatabaseServices.Where(s => s.State == "Queued" || s.State == "Provisioning")
                .ToListAsync(ct)
        )
        {
            service.State = "Provisioning";
            await db.SaveChangesAsync(ct);
            try
            {
                await Databases.Provision(service, ct);
                service.State = "Running";
                service.Error = null;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                service.State = "Failed";
                service.Error =
                    "Database provisioning failed. Check Docker availability and retry.";
                logger.LogWarning(
                    "Database {Id} provisioning failed ({Type}).",
                    service.Id,
                    error.GetType().Name
                );
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Connects a container to project-managed databases only when all required services are running.
    /// </summary>
    private async Task AttachDatabases(
        ForgeDockDbContext db,
        Guid project,
        string container,
        CancellationToken ct
    )
    {
        var services = await db.DatabaseServices.Where(s => s.ProjectId == project).ToListAsync(ct);
        if (services.Count == 0)
            return;
        foreach (var service in services)
        {
            if (service.State != "Running")
                throw new InvalidOperationException(
                    "A managed database is not running. Start or retry it before deploying."
                );
            await Databases.AssertOwned(service, ct);
        }
        await Databases.Connect(project, container, ct);
    }
}
