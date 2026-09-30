using System.Security.Cryptography;
using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ForgeDock.Api;
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/previews", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            var previews = await db.Projects.AsNoTracking().Where(p => p.ParentProjectId == id).Select(p => new { p.Id, p.Name, p.Branch, p.PullRequestNumber, p.HealthStatus }).ToListAsync(ct);
            var domains = await db.CustomDomains.AsNoTracking().Where(d => db.Projects.Any(p => p.Id == d.ProjectId && p.ParentProjectId == id)).Select(d => new { d.ProjectId, d.Hostname }).ToListAsync(ct);
            return Results.Ok(new { enabled = project.PreviewsEnabled, previews, domains });
        });
        api.MapPut("/projects/{id:guid}/previews", async (Guid id, PreviewRequest request, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.FindAsync([id], ct); if (project is null) return Results.NotFound();
            if (project.ParentProjectId is not null) return Results.Conflict(new { error = "Preview projects cannot create nested previews." });
            if (request.Enabled && !await db.ProjectWebhooks.AnyAsync(w => w.ProjectId == id && w.Enabled, ct)) return Results.Conflict(new { error = "Enable GitHub auto-deploy in Settings first." });
            project.PreviewsEnabled = request.Enabled; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
    }
    // Called under the receiver's parent-project transaction and row lock.
    public static async Task<(string Status, Deployment? Deployment)> Handle(Project parent, PullRequestEvent request, ForgeDockDbContext db, SecretProtector protector, IConfiguration configuration, CancellationToken ct)
    {
        if (request.Status is not ("PreviewClosed" or "PreviewQueued")) return (request.Status, null);
        if (parent.ParentProjectId is not null || !parent.PreviewsEnabled && request.Status != "PreviewClosed") return ("IgnoredPreviewsDisabled", null);
        var registration = await db.PreviewEnvironments.FindAsync([parent.Id, request.Number], ct);
        if (registration is not null && (request.UpdatedAt < registration.LastEventAt || request.UpdatedAt == registration.LastEventAt && registration.Closed)) return ("IgnoredStalePreview", null);
        if (registration is null) { registration = new PreviewEnvironment { ParentProjectId = parent.Id, Number = request.Number }; db.PreviewEnvironments.Add(registration); }
        var project = registration.ProjectId is { } id ? await db.Projects.FindAsync([id], ct) : null;
        if (request.Status == "PreviewClosed")
        {
            registration.Closed = true; registration.LastEventAt = request.UpdatedAt;
            if (project is not null && !await db.Operations.AnyAsync(o => o.ProjectId == project.Id && o.Kind == ProjectOperationKind.Delete && o.State != ProjectOperationState.Failed, ct))
                db.Operations.Add(new ProjectOperation { ProjectId = project.Id, Kind = ProjectOperationKind.Delete });
            if (project is not null)
                foreach (var queued in await db.Deployments.Where(d => d.ProjectId == project.Id && d.State == DeploymentState.Queued).ToListAsync(ct)) queued.TransitionTo(DeploymentState.Cancelled);
            return ("PreviewClosed", null);
        }
        if (project is not null && await db.Operations.AnyAsync(o => o.ProjectId == project.Id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
            throw new PreviewBusyException();
        registration.Closed = false; registration.LastEventAt = request.UpdatedAt;
        if (project is null)
        {
            project = new Project { Name = parent.Name[..Math.Min(parent.Name.Length, 80)] + $" PR #{request.Number}", ParentProjectId = parent.Id, PullRequestNumber = request.Number };
            db.Projects.Add(project); registration.ProjectId = project.Id;
            foreach (var kind in await db.DatabaseServices.Where(s => s.ProjectId == parent.Id).Select(s => s.Kind).ToListAsync(ct))
            {
                var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var service = new DatabaseService { ProjectId = project.Id, Kind = kind, ProtectedPassword = protector.Protect(password) };
                db.DatabaseServices.Add(service); db.EnvironmentVariables.Add(new ProjectEnvironment { ProjectId = project.Id, Name = DatabaseRuntime.Variable(kind), ProtectedValue = protector.Protect(DatabaseRuntime.Connection(service, password)) });
            }
            var suffix = configuration["ForgeDock:PreviewBaseDomain"];
            if (!string.IsNullOrWhiteSpace(suffix) && DomainName.TryNormalize(suffix, out var domain) && DomainSettings.From(k => configuration[k]).Validate() is null)
                db.CustomDomains.Add(new CustomDomain { ProjectId = project.Id, Hostname = $"pr-{request.Number}-{parent.Id:N}.{domain}", DnsVerifiedAt = DateTimeOffset.UtcNow,
                    VerificationRequested = false, NextCheckAt = DateTimeOffset.MaxValue, State = CustomDomainState.AwaitingDeployment });
        }
        project.RepositoryUrl = parent.RepositoryUrl; project.Branch = request.Branch!; project.DeploymentMode = parent.DeploymentMode;
        project.Dockerfile = parent.Dockerfile; project.ComposeFile = parent.ComposeFile; project.ComposeService = parent.ComposeService;
        project.BuildCommand = parent.BuildCommand; project.StartCommand = parent.StartCommand; project.RootDirectory = parent.RootDirectory;
        project.ContainerPort = parent.ContainerPort; project.HealthPath = parent.HealthPath;
        // Preview secrets are independent; production environment variables are never copied.
        var environment = await db.EnvironmentVariables.Where(e => e.ProjectId == project.Id).ToListAsync(ct);
        environment.AddRange(db.EnvironmentVariables.Local.Where(e => e.ProjectId == project.Id && !environment.Any(existing => existing.Name == e.Name)));
        var deployment = new Deployment { ProjectId = project.Id, RequestedCommit = request.Sha, Trigger = "GitHubPullRequest", ConfigurationJson = DeploymentSnapshot.Create(project, environment).Serialize() };
        db.Deployments.Add(deployment); return ("PreviewQueued", deployment);
    }
}
public sealed record PreviewRequest(bool Enabled);
public sealed class PreviewBusyException : Exception;
