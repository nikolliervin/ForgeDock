using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ForgeDock.Tests;

public class ManagementApiTests
{
    [DockerFact]
    public async Task AuthenticationSnapshotsAndConcurrentQueueCommandsUseTheRealHttpPipeline()
    {
        await using var fixture = new ApiFixture();
        await fixture.Initialize();
        using var unauthenticated = fixture.Factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await unauthenticated.GetAsync("/api/storage")).StatusCode
        );
        var client = fixture.Client;
        var created = await client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = "API review",
                repositoryUrl = "https://github.com/example/app",
                deploymentMode = "Auto",
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await client.PutAsJsonAsync(
                    $"/api/projects/{id}/environment/EXAMPLE",
                    new { value = "review-test-secret" }
                )
            ).StatusCode
        );

        // Both requests must read pending work after taking the same transaction lock.
        var results = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/projects/{id}/deployments", new { }),
            client.PostAsJsonAsync($"/api/projects/{id}/operations", new { kind = "Stop" })
        );
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
        var queued = await db.Deployments.SingleOrDefaultAsync(d => d.ProjectId == id);
        if (queued is null)
        {
            db.Operations.RemoveRange(
                await db.Operations.Where(o => o.ProjectId == id).ToListAsync()
            );
            await db.SaveChangesAsync();
            Assert.Equal(
                HttpStatusCode.Accepted,
                (
                    await client.PostAsJsonAsync($"/api/projects/{id}/deployments", new { })
                ).StatusCode
            );
            queued = await db.Deployments.SingleAsync(d => d.ProjectId == id);
        }
        {
            Assert.DoesNotContain("review-test-secret", queued.ConfigurationJson);
            var snapshot = DeploymentSnapshot.Deserialize(queued.ConfigurationJson);
            Assert.Equal(
                "review-test-secret",
                fixture.Protector.Unprotect(snapshot.ProtectedEnvironment["EXAMPLE"])
            );
            Assert.DoesNotContain(
                "ConfigurationJson",
                await (
                    await client.GetAsync($"/api/deployments/{queued.Id}")
                ).Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase
            );
        }
        Assert.DoesNotContain(
            "review-test-secret",
            await (
                await client.GetAsync($"/api/projects/{id}/environment")
            ).Content.ReadAsStringAsync()
        );
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{id}")).StatusCode);
    }

    [DockerFact]
    public async Task TaskQueuePreventsDuplicateRunsDeletionAndStopWhilePreservingSecretSnapshots()
    {
        await using var fixture = new ApiFixture();
        await fixture.Initialize();
        var client = fixture.Client;
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
        var project = new Project
        {
            Name = "Task API review",
            RepositoryUrl = "https://github.com/example/app",
        };
        var source = StorageRetentionTests.Successful(project.Id, "retained-image", 1);
        source.ConfigurationJson = DeploymentSnapshot
            .Create(
                project,
                [
                    new ProjectEnvironment
                    {
                        Name = "EXAMPLE",
                        ProtectedValue = fixture.Protector.Protect("task-review-secret"),
                    },
                ]
            )
            .Serialize();
        project.ActiveDeploymentId = source.Id;
        db.Projects.Add(project);
        db.Deployments.Add(source);
        await db.SaveChangesAsync();
        var created = await client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/jobs",
            new
            {
                name = "Maintenance",
                command = "echo $EXAMPLE",
                intervalMinutes = 0,
                timeoutSeconds = 30,
            }
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        var path = $"/api/projects/{project.Id}/jobs/{jobId}";
        var results = await Task.WhenAll(
            client.PostAsJsonAsync(path + "/run", new { }),
            client.PostAsJsonAsync(path + "/run", new { })
        );
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project.Id}/operations",
                    new { kind = "Delete" }
                )
            ).StatusCode
        );
        var run = await db.JobRuns.SingleAsync();
        Assert.Equal(source.ImageTag, run.ImageTag);
        Assert.DoesNotContain("task-review-secret", run.ConfigurationJson);
        var json = await (
            await client.GetAsync($"/api/projects/{project.Id}/jobs")
        ).Content.ReadAsStringAsync();
        Assert.DoesNotContain("configurationJson", json);
        Assert.DoesNotContain("task-review-secret", json);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(
                    $"/api/projects/{project.Id}/jobs",
                    new
                    {
                        name = "",
                        command = "",
                        intervalMinutes = -1,
                        timeoutSeconds = 0,
                    }
                )
            ).StatusCode
        );
    }
}

// Uses an isolated migrated PostgreSQL database, never the operator's project records.
internal sealed class ApiFixture : IAsyncDisposable
{
    private readonly string database = "fd_api_review_" + Guid.NewGuid().ToString("N");
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private NpgsqlConnection admin = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public SecretProtector Protector => new(key);

    public async Task Initialize()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock")!;
        admin = new(
            new NpgsqlConnectionStringBuilder(original) { Database = "postgres" }.ConnectionString
        );
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await command.ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(original)
        {
            Database = database,
        }.ConnectionString;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ForgeDock", connection);
            builder.UseSetting("ForgeDock:ApiToken", new string('x', 64));
            builder.UseSetting("ForgeDock:SecretKey", key);
            builder.UseSetting("ForgeDock:WebRoot", "");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        });
        using var scope = Factory.Services.CreateScope();
        await scope
            .ServiceProvider.GetRequiredService<ForgeDockDbContext>()
            .Database.MigrateAsync();
        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new string('x', 64)
        );
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (admin is not null)
        {
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {database} WITH (FORCE)",
                admin
            );
            await command.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }
}
