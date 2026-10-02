using System.Text.Json.Serialization;
using ForgeDock.Api;
using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var connection =
    builder.Configuration.GetConnectionString("ForgeDock")
    ?? throw new InvalidOperationException(
        "Set ConnectionStrings__ForgeDock to a PostgreSQL connection string."
    );
builder.Services.AddSingleton(
    new SecretProtector(
        builder.Configuration["ForgeDock:SecretKey"]
            ?? throw new InvalidOperationException(
                "Set ForgeDock__SecretKey with openssl rand -base64 32."
            )
    )
);
builder.Services.AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection));
builder.Services.AddSingleton(DomainSettings.From(key => builder.Configuration[key]));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())
);
var sso = builder.AddManagementAuthentication();
builder.Services.AddAuthorization();
var app = builder.Build();
if (sso)
    app.UseForwardedHeaders();
app.UseExceptionHandler();
var webRoot = builder.Configuration["ForgeDock:WebRoot"];
if (!string.IsNullOrWhiteSpace(webRoot) && Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapGet(
            "/docs/{**path}",
            () => Results.File(Path.Combine(Path.GetFullPath(webRoot), "index.html"), "text/html")
        )
        .AllowAnonymous();
    app.MapGet(
            "/storage",
            () => Results.File(Path.Combine(Path.GetFullPath(webRoot), "index.html"), "text/html")
        )
        .AllowAnonymous();
    app.MapGet(
            "/projects/{**path}",
            () => Results.File(Path.Combine(Path.GetFullPath(webRoot), "index.html"), "text/html")
        )
        .AllowAnonymous();
}
app.UseStatusCodePages();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
if (sso)
    app.Use(ManagementAuthentication.ValidateManagementCsrf);
app.MapManagementAuthentication(sso);
app.MapOpenApi().RequireAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet(
    "/health/ready",
    async (ForgeDockDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(503)
);
var api = app.MapGroup("/api").RequireAuthorization();
api.MapDomainEndpoints();
api.MapMetricsEndpoints();
api.MapEnvironmentEndpoints();
api.MapConsoleEndpoints();
api.MapWebhookEndpoints();
api.MapNotificationEndpoints();
api.MapDatabaseEndpoints();
api.MapBackupEndpoints();
api.MapStorageEndpoints();
api.MapReleaseEndpoints();
api.MapHookEndpoints();
api.MapRollbackEndpoints();
api.MapJobEndpoints();
api.MapPreviewEndpoints();
api.MapResourceEndpoints();
api.MapTemplateEndpoints();
api.MapGet(
    "/session",
    (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var csrf = sso
            ? context
                .RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()
                .GetAndStoreTokens(context)
                .RequestToken
            : null;
        return Results.Ok(new { name = context.User.Identity?.Name, csrfToken = csrf });
    }
);
api.MapProjectEndpoints();
api.MapDeploymentEndpoints();
api.MapProjectEnvironmentEndpoints();
app.Run();

// Enables isolated HTTP integration tests to host the real pipeline.
public partial class Program;
