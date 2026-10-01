using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ForgeDock.Api;

public static class DomainEndpoints
{
    /// <summary>
    /// Registers hostname normalization, ownership-verification requests, and asynchronous domain removal;
    /// the worker applies proxy and certificate state.
    /// </summary>
    public static void MapDomainEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/hosting",
            (DomainSettings settings) =>
                new
                {
                    enabled = settings.Enabled,
                    ready = settings.Validate() is null,
                    setupMessage = settings.Validate(),
                    target = settings.Target,
                    addresses = settings.Addresses,
                    stagingCertificates = settings.IsStaging,
                }
        );
        api.MapGet(
            "/projects/{id:guid}/domains",
            async (Guid id, ForgeDockDbContext db, DomainSettings settings, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                return Results.Ok(
                    (
                        await db
                            .CustomDomains.AsNoTracking()
                            .Where(d => d.ProjectId == id)
                            .OrderBy(d => d.CreatedAt)
                            .ToListAsync(ct)
                    ).Select(domain => DomainResponse.From(domain, settings))
                );
            }
        );
        api.MapPost(
            "/projects/{id:guid}/domains",
            async (
                Guid id,
                DomainRequest request,
                ForgeDockDbContext db,
                DomainSettings settings,
                CancellationToken ct
            ) =>
            {
                if (settings.Validate() is { } configurationError)
                    return Results.Conflict(new { error = configurationError });
                if (!DomainName.TryNormalize(request.Hostname, out var hostname))
                    return Results.Problem(
                        "Enter a public hostname such as app.example.com, without a scheme, port, path, or wildcard.",
                        statusCode: 400
                    );
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                if (await db.CustomDomains.CountAsync(d => d.ProjectId == id, ct) >= 20)
                    return Results.Conflict(
                        new { error = "A project can have up to 20 custom domains." }
                    );
                var domain = new CustomDomain
                {
                    ProjectId = id,
                    Hostname = hostname,
                    VerificationToken =
                        "forgedock-verification="
                        + Convert
                            .ToHexString(RandomNumberGenerator.GetBytes(32))
                            .ToLowerInvariant(),
                };
                db.CustomDomains.Add(domain);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException error)
                    when (error.InnerException is PostgresException { SqlState: "23505" })
                {
                    return Results.Conflict(
                        new
                        {
                            error = "This domain is already attached to a project. Remove it there before adding it again.",
                        }
                    );
                }
                return Results.Created(
                    $"/api/projects/{id}/domains/{domain.Id}",
                    DomainResponse.From(domain, settings)
                );
            }
        );
        api.MapPost(
            "/projects/{id:guid}/domains/{domainId:guid}/verify",
            async (Guid id, Guid domainId, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var domain = await db.CustomDomains.SingleOrDefaultAsync(
                    d => d.Id == domainId && d.ProjectId == id,
                    ct
                );
                if (domain is null)
                    return Results.NotFound();
                if (domain.State == CustomDomainState.Removing)
                    return Results.Conflict(new { error = "Domain removal is in progress." });
                domain.VerificationRequested = true;
                domain.NextCheckAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Accepted(value: new { domain.Id, domain.State });
            }
        );
        api.MapDelete(
            "/projects/{id:guid}/domains/{domainId:guid}",
            async (Guid id, Guid domainId, ForgeDockDbContext db, CancellationToken ct) =>
            {
                var domain = await db.CustomDomains.SingleOrDefaultAsync(
                    d => d.Id == domainId && d.ProjectId == id,
                    ct
                );
                if (domain is null)
                    return Results.NotFound();
                domain.State = CustomDomainState.Removing;
                domain.NextCheckAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Accepted(value: new { domain.Id, domain.State });
            }
        );
    }
}

public record DomainRequest(string Hostname);

public record DomainRecord(string Type, string Name, string Value);

public record DomainResponse(
    Guid Id,
    string Hostname,
    CustomDomainState State,
    bool VerificationRequested,
    DateTimeOffset? DnsVerifiedAt,
    DateTimeOffset? CertificateExpiresAt,
    bool CertificateTrusted,
    string? Error,
    IReadOnlyList<DomainRecord> DnsRecords
)
{
    public static DomainResponse From(CustomDomain domain, DomainSettings settings)
    {
        var records = new List<DomainRecord>
        {
            new("TXT", "_forgedock." + domain.Hostname, domain.VerificationToken),
        };
        foreach (var address in settings.Addresses)
            if (IPAddress.TryParse(address, out var ip))
                records.Add(
                    new(
                        ip.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA",
                        domain.Hostname,
                        address
                    )
                );
        return new(
            domain.Id,
            domain.Hostname,
            domain.State,
            domain.VerificationRequested,
            domain.DnsVerifiedAt,
            domain.CertificateExpiresAt,
            domain.CertificateTrusted,
            domain.Error,
            records
        );
    }
}
