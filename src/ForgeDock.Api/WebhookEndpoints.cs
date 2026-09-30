using System.Security.Cryptography;
using System.Text.Json;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class WebhookEndpoints
{
    private const int MaxPayload = 2 * 1024 * 1024;
    private static string HookPath(Guid id) => $"/api/webhooks/github/{id}";

    public static void MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/webhook", async (Guid id, ForgeDockDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!await db.Projects.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
            return Results.Ok(await Settings(id, db, configuration, null, ct));
        });
        api.MapPut("/projects/{id:guid}/webhook", async (Guid id, WebhookSettingsRequest request, ForgeDockDbContext db,
            SecretProtector protector, IConfiguration configuration, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var project = await LockProject(id, db, ct);
            if (project is null) return Results.NotFound();
            if (request.Enabled && GitHubWebhook.RepositoryIdentity(project.RepositoryUrl) is null)
                return Results.Problem("Auto-deploy requires a github.com HTTPS repository URL.", statusCode: 400);
            var hook = await db.ProjectWebhooks.FindAsync([id], ct);
            string? secret = null;
            if (hook is null)
            {
                if (!request.Enabled) return Results.Ok(await Settings(id, db, configuration, null, ct));
                secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                hook = new ProjectWebhook { ProjectId = id, ProtectedSecret = protector.Protect(secret) };
                db.ProjectWebhooks.Add(hook);
            }
            hook.Enabled = request.Enabled;
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(await Settings(id, db, configuration, secret, ct));
        });
        api.MapPost("/projects/{id:guid}/webhook/rotate-secret", async (Guid id, ForgeDockDbContext db,
            SecretProtector protector, IConfiguration configuration, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await LockProject(id, db, ct) is null) return Results.NotFound();
            var hook = await db.ProjectWebhooks.FindAsync([id], ct);
            if (hook is null) return Results.Conflict(new { error = "Enable auto-deploy first." });
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            hook.ProtectedSecret = protector.Protect(secret);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(await Settings(id, db, configuration, secret, ct));
        });
        // GitHub authenticates this route with the body signature, rather than the management token.
        api.MapPost("/webhooks/github/{id:guid}", Receive).AllowAnonymous()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxPayload));
    }

    private static Task<Project?> LockProject(Guid id, ForgeDockDbContext db, CancellationToken ct) =>
        db.Projects.FromSqlInterpolated($"SELECT * FROM \"Projects\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    private static async Task<object> Settings(Guid id, ForgeDockDbContext db, IConfiguration configuration, string? secret, CancellationToken ct)
    {
        var hook = await db.ProjectWebhooks.AsNoTracking().SingleOrDefaultAsync(w => w.ProjectId == id, ct);
        var last = await db.WebhookDeliveries.AsNoTracking().Where(w => w.ProjectId == id).OrderByDescending(w => w.ReceivedAt)
            .Select(w => new { w.DeliveryId, w.ReceivedAt, w.Event, w.Status, w.DeploymentId }).FirstOrDefaultAsync(ct);
        var baseUrl = configuration["ForgeDock:WebhookBaseUrl"]?.TrimEnd('/');
        var publicUrl = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? baseUrl + HookPath(id) : null;
        return new { enabled = hook?.Enabled ?? false, configured = hook is not null, path = HookPath(id), publicUrl, secret, lastDelivery = last };
    }

    private static async Task<IResult> Receive(Guid id, HttpRequest request, ForgeDockDbContext db, SecretProtector protector, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) return Results.Problem("Use application/json webhook payloads.", statusCode: 415);
        if (request.ContentLength > MaxPayload) return Results.StatusCode(413);
        var hook = await db.ProjectWebhooks.AsNoTracking().SingleOrDefaultAsync(w => w.ProjectId == id, ct);
        if (hook is null) return Results.NotFound();
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxPayload) return Results.StatusCode(413);
            body.Write(buffer, 0, read);
        }
        var bytes = body.ToArray();
        if (!GitHubWebhook.VerifySignature(bytes, protector.Unprotect(hook.ProtectedSecret), request.Headers["X-Hub-Signature-256"].ToString()))
            return Results.Unauthorized();
        if (!Guid.TryParse(request.Headers["X-GitHub-Delivery"], out var deliveryId))
            return Results.Problem("A valid X-GitHub-Delivery ID is required.", statusCode: 400);
        var eventName = request.Headers["X-GitHub-Event"].ToString();
        if (string.IsNullOrWhiteSpace(eventName) || eventName.Length > 100) return Results.Problem("A valid GitHub event is required.", statusCode: 400);

        // Serialize deliveries and settings changes for this project. The composite receipt key
        // also enforces idempotency across API instances and process restarts.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var project = await LockProject(id, db, ct);
        if (project is null) return Results.NotFound();
        hook = await db.ProjectWebhooks.AsNoTracking().SingleAsync(w => w.ProjectId == id, ct);
        // A secret may have rotated while the request body was being read.
        if (!GitHubWebhook.VerifySignature(bytes, protector.Unprotect(hook.ProtectedSecret), request.Headers["X-Hub-Signature-256"].ToString()))
            return Results.Unauthorized();
        var previous = await db.WebhookDeliveries.FindAsync([id, deliveryId], ct);
        if (previous is not null) return Results.Ok(new { status = "Duplicate", previous.DeploymentId });
        string status; string? commit = null; PullRequestEvent? pullRequest = null;
        try
        {
            if (!hook.Enabled) status = "IgnoredDisabled";
            else if (eventName == "ping") { using var ping = JsonDocument.Parse(bytes); status = "Ping"; }
            else if (eventName == "pull_request") { pullRequest = PullRequestWebhook.Evaluate(bytes, project.RepositoryUrl, project.Branch); status = pullRequest.Status; }
            else if (eventName != "push") status = "IgnoredEvent";
            else (status, commit) = GitHubWebhook.EvaluatePush(bytes, project.RepositoryUrl, project.Branch);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return Results.Problem("Malformed GitHub webhook payload.", statusCode: 400); }
        Deployment? deployment = null;
        if (pullRequest is not null)
        {
            try { (status, deployment) = await PreviewEndpoints.Handle(project, pullRequest, db, protector,
                request.HttpContext.RequestServices.GetRequiredService<IConfiguration>(), ct); }
            catch (PreviewBusyException) { return Results.Conflict(new { error = "Preview cleanup pending. Redeliver after it finishes." }); }
        }
        if (commit is not null)
        {
            if (await db.Operations.AnyAsync(o => o.ProjectId == id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
                return Results.Conflict(new { error = "Project operation pending. Redeliver this webhook after it finishes." });
            var environment = await db.EnvironmentVariables.Where(e => e.ProjectId == id).ToListAsync(ct);
            deployment = new Deployment { ProjectId = id, RequestedCommit = commit, Trigger = "GitHubPush",
                ConfigurationJson = DeploymentSnapshot.Create(project, environment).Serialize() };
            db.Deployments.Add(deployment);
        }
        db.WebhookDeliveries.Add(new WebhookDelivery { ProjectId = id, DeliveryId = deliveryId, Event = eventName,
            Status = status, DeploymentId = deployment?.Id });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Results.Ok(new { status, deploymentId = deployment?.Id });
    }
}

public sealed record WebhookSettingsRequest(bool Enabled);
