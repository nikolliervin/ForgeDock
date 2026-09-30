using ForgeDock.Api;
using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("ForgeDock")
    ?? throw new InvalidOperationException("Set ConnectionStrings__ForgeDock to a PostgreSQL connection string.");
var token = builder.Configuration["ForgeDock:ApiToken"];
if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
    throw new InvalidOperationException("Set ForgeDock__ApiToken to a random token of at least 32 characters.");
builder.Services.AddSingleton(new SecretProtector(builder.Configuration["ForgeDock:SecretKey"]
    ?? throw new InvalidOperationException("Set ForgeDock__SecretKey with openssl rand -base64 32.")));
builder.Services.AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection));
builder.Services.AddSingleton(DomainSettings.From(key => builder.Configuration[key]));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthentication("ManagementToken").AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>("ManagementToken", _ => { });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseExceptionHandler();
var webRoot = builder.Configuration["ForgeDock:WebRoot"];
if (!string.IsNullOrWhiteSpace(webRoot) && Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapGet("/docs/{**path}", () => Results.File(Path.Combine(Path.GetFullPath(webRoot), "index.html"), "text/html")).AllowAnonymous();
}
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi().RequireAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (ForgeDockDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
var api = app.MapGroup("/api").RequireAuthorization();
api.MapDomainEndpoints();
api.MapMetricsEndpoints();
api.MapEnvironmentEndpoints();
api.MapGet("/session", () => new { name = "operator" });
api.MapGet("/projects", async (ForgeDockDbContext db, CancellationToken ct) =>
    await db.Projects.AsNoTracking().OrderByDescending(p => p.CreatedAt).Select(p => new ProjectResponse(
        p.Id, p.Name, p.RepositoryUrl, p.Branch, p.Dockerfile, p.ContainerPort, p.HealthPath, p.ActiveDeploymentId, p.HealthStatus, p.DeploymentMode, p.ComposeFile, p.ComposeService, p.BuildCommand, p.StartCommand, p.RootDirectory)).ToListAsync(ct));
api.MapPost("/projects", async (ProjectRequest request, ForgeDockDbContext db, CancellationToken ct) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = errors.ToArray() });
    var project = new Project { Name = request.Name, RepositoryUrl = request.RepositoryUrl, Branch = request.Branch,
        DeploymentMode = request.DeploymentMode, ComposeFile = request.ComposeFile, ComposeService = request.ComposeService,
        BuildCommand = request.BuildCommand, StartCommand = request.StartCommand, RootDirectory = request.RootDirectory, Dockerfile = request.Dockerfile, ContainerPort = request.ContainerPort, HealthPath = request.HealthPath };
    db.Projects.Add(project);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/projects/{project.Id}", ProjectResponse.From(project));
});
api.MapGet("/projects/{id:guid}", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
    await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } project
        ? Results.Ok(ProjectResponse.From(project)) : Results.NotFound());
api.MapPut("/projects/{id:guid}", async (Guid id, ProjectRequest request, ForgeDockDbContext db, CancellationToken ct) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = errors.ToArray() });
    var project = await db.Projects.FindAsync([id], ct);
    if (project is null) return Results.NotFound();
    project.Name = request.Name; project.RepositoryUrl = request.RepositoryUrl; project.Branch = request.Branch;
    project.DeploymentMode = request.DeploymentMode; project.ComposeFile = request.ComposeFile; project.ComposeService = request.ComposeService;
    project.BuildCommand = request.BuildCommand; project.StartCommand = request.StartCommand; project.RootDirectory = request.RootDirectory;
    project.Dockerfile = request.Dockerfile; project.ContainerPort = request.ContainerPort; project.HealthPath = request.HealthPath;
    await db.SaveChangesAsync(ct);
    return Results.Ok(ProjectResponse.From(project));
});
api.MapPost("/projects/{id:guid}/deployments", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
{
    var project = await db.Projects.FindAsync([id], ct);
    if (project is null) return Results.NotFound();
    if (await db.Operations.AnyAsync(o => o.ProjectId == id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
        return Results.Conflict(new { error = "Wait for the pending project operation." });
    var environment = await db.EnvironmentVariables.Where(e => e.ProjectId == id).ToListAsync(ct);
    var deployment = new Deployment { ProjectId = id, ConfigurationJson = DeploymentSnapshot.Create(project, environment).Serialize() };
    db.Deployments.Add(deployment);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/deployments/{deployment.Id}", DeploymentResponse.From(deployment));
});
api.MapGet("/projects/{id:guid}/deployments", async (Guid id, int? limit, ForgeDockDbContext db, CancellationToken ct) =>
    (await db.Deployments.AsNoTracking().Where(d => d.ProjectId == id).OrderByDescending(d => d.CreatedAt)
        .Take(Math.Clamp(limit ?? 50, 1, 100)).ToListAsync(ct)).Select(DeploymentResponse.From));
api.MapGet("/deployments/{id:guid}", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
    await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } deployment
        ? Results.Ok(DeploymentResponse.From(deployment)) : Results.NotFound());
api.MapGet("/deployments/{id:guid}/logs", async (Guid id, long? after, ForgeDockDbContext db, CancellationToken ct) =>
    await db.Logs.AsNoTracking().Where(l => l.DeploymentId == id && l.Id > (after ?? 0)).OrderBy(l => l.Id)
        .Take(500).Select(l => new { l.Id, l.Timestamp, l.Message }).ToListAsync(ct));
api.MapPost("/deployments/{id:guid}/rollback", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
{
    var source = await db.Deployments.FindAsync([id], ct);
    if (source is null) return Results.NotFound();
    if (source.State is not (DeploymentState.Running or DeploymentState.Stopped) || source.ImageTag is null)
        return Results.Conflict(new { error = "Rollback requires an earlier successful retained image." });
    if (await db.Operations.AnyAsync(o => o.ProjectId == source.ProjectId && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
        return Results.Conflict(new { error = "Wait for the pending project operation." });
    var deployment = new Deployment { ProjectId = source.ProjectId, RollbackSourceId = source.Id,
        ImageTag = source.ImageTag, CommitSha = source.CommitSha, ConfigurationJson = source.ConfigurationJson, ProtectedComposeManifest = source.ProtectedComposeManifest };
    db.Deployments.Add(deployment);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/deployments/{deployment.Id}", DeploymentResponse.From(deployment));
});
api.MapPost("/projects/{id:guid}/restart", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
{
    var project = await db.Projects.FindAsync([id], ct);
    if (project?.ActiveDeploymentId is not { } active) return Results.Conflict(new { error = "Project has no retained active version to restart." });
    if (await db.Operations.AnyAsync(o => o.ProjectId == id && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
        return Results.Conflict(new { error = "Wait for the pending project operation." });
    var source = await db.Deployments.FindAsync([active], ct);
    if (source?.ImageTag is null) return Results.Conflict(new { error = "Active image is unavailable." });
    var deployment = new Deployment { ProjectId = id, RollbackSourceId = source.Id, ImageTag = source.ImageTag,
        CommitSha = source.CommitSha, ConfigurationJson = source.ConfigurationJson, ProtectedComposeManifest = source.ProtectedComposeManifest };
    db.Deployments.Add(deployment); await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/deployments/{deployment.Id}", DeploymentResponse.From(deployment));
});
api.MapPost("/projects/{id:guid}/operations", async (Guid id, OperationRequest request, ForgeDockDbContext db, CancellationToken ct) =>
{
    if (!Enum.IsDefined(request.Kind)) return Results.Problem("Unsupported project operation.", statusCode: 400);
    if (!await db.Projects.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
    if (await db.Deployments.AnyAsync(d => d.ProjectId == id && d.State != DeploymentState.Running &&
        d.State != DeploymentState.Failed && d.State != DeploymentState.Stopped, ct))
        return Results.Conflict(new { error = "Wait for queued or active deployments before stopping or deleting the project." });
    var operation = new ProjectOperation { ProjectId = id, Kind = request.Kind };
    db.Operations.Add(operation); await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/operations/{operation.Id}", new { operation.Id, operation.State });
});
api.MapGet("/operations/{id:guid}", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
    await db.Operations.AsNoTracking().Where(o => o.Id == id).Select(o => new { o.Id, o.ProjectId, o.Kind, o.State, o.Error }).SingleOrDefaultAsync(ct)
        is { } operation ? Results.Ok(operation) : Results.NotFound());
api.MapGet("/projects/{id:guid}/environment", async (Guid id, ForgeDockDbContext db, CancellationToken ct) =>
    await db.EnvironmentVariables.Where(e => e.ProjectId == id).Select(e => e.Name).OrderBy(n => n).ToListAsync(ct));
api.MapPut("/projects/{id:guid}/environment/{name}", async (Guid id, string name, EnvironmentRequest request,
    ForgeDockDbContext db, SecretProtector protector, CancellationToken ct) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$") ||
        request.Value is null || request.Value.Length > 16384 || request.Value.Any(c => c is '\r' or '\n' or '\0'))
        return Results.Problem("Use a valid variable name and a single-line value up to 16 KiB.", statusCode: 400);
    if (!await db.Projects.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
    var variable = await db.EnvironmentVariables.FindAsync([id, name], ct);
    if (variable is null) { variable = new ProjectEnvironment { ProjectId = id, Name = name }; db.EnvironmentVariables.Add(variable); }
    variable.ProtectedValue = protector.Protect(request.Value);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});
api.MapDelete("/projects/{id:guid}/environment/{name}", async (Guid id, string name, ForgeDockDbContext db, CancellationToken ct) =>
{
    var variable = await db.EnvironmentVariables.FindAsync([id, name], ct);
    if (variable is null) return Results.NotFound();
    db.EnvironmentVariables.Remove(variable); await db.SaveChangesAsync(ct); return Results.NoContent();
});
app.Run();
public record EnvironmentRequest(string Value);
public record OperationRequest(ProjectOperationKind Kind);

public record ProjectRequest(string Name, string RepositoryUrl, string Branch = "main", string Dockerfile = "Dockerfile",
    int ContainerPort = 8080, string HealthPath = "/", DeploymentMode DeploymentMode = DeploymentMode.Dockerfile,
    string ComposeFile = "docker-compose.yml", string ComposeService = "", string BuildCommand = "", string StartCommand = "", string RootDirectory = ".")
{
    public IReadOnlyList<string> Validate() => ProjectConfiguration.Validate(Name ?? "", RepositoryUrl ?? "", Branch ?? "",
        Dockerfile ?? "", ContainerPort, HealthPath ?? "", DeploymentMode, ComposeFile ?? "", ComposeService ?? "", BuildCommand, StartCommand, RootDirectory);
}
public record ProjectResponse(Guid Id, string Name, string RepositoryUrl, string Branch, string Dockerfile,
    int ContainerPort, string HealthPath, Guid? ActiveDeploymentId, string HealthStatus,
    DeploymentMode DeploymentMode, string ComposeFile, string ComposeService, string BuildCommand, string StartCommand, string RootDirectory)
{
    public static ProjectResponse From(Project p) => new(p.Id, p.Name, p.RepositoryUrl, p.Branch, p.Dockerfile,
        p.ContainerPort, p.HealthPath, p.ActiveDeploymentId, p.HealthStatus, p.DeploymentMode, p.ComposeFile, p.ComposeService, p.BuildCommand, p.StartCommand, p.RootDirectory);
}
public record DeploymentResponse(Guid Id, Guid ProjectId, DeploymentState State, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? CommitSha, string? Error, Guid? RollbackSourceId, IReadOnlyList<ServiceStatus> Services)
{
    public static DeploymentResponse From(Deployment d) => new(d.Id, d.ProjectId, d.State, d.CreatedAt,
        d.UpdatedAt, d.CommitSha, d.Error, d.RollbackSourceId,
        string.IsNullOrWhiteSpace(d.ServiceStatusJson) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<ServiceStatus>>(d.ServiceStatusJson) ?? []);
}
