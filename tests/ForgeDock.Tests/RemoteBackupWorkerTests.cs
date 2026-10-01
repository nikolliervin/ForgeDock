using System.Reflection;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using DeploymentWorker = ForgeDock.Worker.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ForgeDock.Tests;
public class RemoteBackupWorkerTests
{
    [RemoteBackupFact]
    public async Task FailedUploadsRetryAndRemoteOnlyBackupsRestoreThroughWorker()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock")!;
        var endpoint = Environment.GetEnvironmentVariable("FORGEDOCK_TEST_S3_ENDPOINT")!;
        var database = "fd_backup_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(original) { Database = database };
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(original) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync(); await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await command.ExecuteNonQueryAsync();
        var root = Path.Combine(Path.GetTempPath(), database);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var protector = new SecretProtector(key); var runner = new ProcessRunner();
        var service = new DatabaseService { ProjectId = Guid.NewGuid(), Kind = DatabaseKind.PostgreSql, State = "Running" };
        var runtime = new DatabaseRuntime(runner, protector, root);
        var bucket = "fd-test-" + Guid.NewGuid().ToString("N");
        using var client = new AmazonS3Client(new BasicAWSCredentials("test", "test-secret"), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForgeDock:RuntimePath"] = root, ["ForgeDock:SecretKey"] = key, ["ForgeDock:BackupRetentionCount"] = "1", ["ForgeDock:BackupLocalRetentionCount"] = "0",
            ["ForgeDock:BackupS3:Enabled"] = "true", ["ForgeDock:BackupS3:Endpoint"] = endpoint, ["ForgeDock:BackupS3:Region"] = "us-east-1",
            ["ForgeDock:BackupS3:Bucket"] = bucket, ["ForgeDock:BackupS3:AccessKey"] = "test", ["ForgeDock:BackupS3:SecretKey"] = "test-secret", ["ForgeDock:BackupS3:AllowHttp"] = "true"
        }).Build();
        await using var provider = new ServiceCollection().AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection.ConnectionString)).BuildServiceProvider();
        var worker = new DeploymentWorker(provider.GetRequiredService<IServiceScopeFactory>(), config, runner, protector, NullLogger<DeploymentWorker>.Instance);
        Task Invoke(string name, ForgeDockDbContext db) => (Task)typeof(DeploymentWorker).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, [db, CancellationToken.None])!;
        Task<string> Query(string sql) => runtime.Docker(["exec", DatabaseRuntime.Container(service), "psql", "-U", "app", "-d", "app", "-Atc", sql], default);
        try
        {
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>(); await db.Database.MigrateAsync();
            db.Projects.Add(new Project { Id = service.ProjectId, Name = "Remote backup test", RepositoryUrl = "https://github.com/example/test" }); db.DatabaseServices.Add(service);
            var backup = new DatabaseBackup { ProjectId = service.ProjectId, ServiceId = service.Id }; db.DatabaseBackups.Add(backup); await db.SaveChangesAsync();
            await runtime.Docker(["run", "-d", "--name", DatabaseRuntime.Container(service), "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={service.ProjectId}",
                "--label", $"io.forgedock.database={service.Id}", "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "-e", "POSTGRES_USER=app", "-e", "POSTGRES_DB=app", "postgres:17-alpine"], default);
            for (var i = 0; ; i++)
            {
                try { await Query("SELECT 1"); break; }
                catch (InvalidOperationException) when (i < 40) { await Task.Delay(250); }
            }
            await Query("CREATE TABLE sample(value text); INSERT INTO sample VALUES ('original');");
            await Invoke("ProcessBackups", db); await db.Entry(backup).ReloadAsync();
            Assert.Equal("Completed", backup.State); Assert.Equal("Failed", backup.RemoteState);
            var archive = new BackupRuntime(runtime, root, key).FilePath(backup.Id); Assert.True(File.Exists(archive));
            Assert.NotNull(backup.RemoteNextAttemptAt); Assert.DoesNotContain("test-secret", backup.RemoteError);
            await client.PutBucketAsync(bucket); backup.RemoteNextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
            await Invoke("SynchronizeRemoteBackups", db); await Invoke("ApplyBackupRetention", db);
            Assert.Equal("Uploaded", backup.RemoteState); Assert.False(File.Exists(archive));
            await Query("UPDATE sample SET value='modified'");
            var restore = new DatabaseBackup { ProjectId = service.ProjectId, ServiceId = service.Id, Kind = "Restore", SourceBackupId = backup.Id }; db.DatabaseBackups.Add(restore); await db.SaveChangesAsync();
            await Invoke("ProcessBackups", db); Assert.Equal("Completed", restore.State); Assert.Equal("original", await Query("SELECT value FROM sample"));
            // The next successful upload expires the old remote snapshot.
            var next = new DatabaseBackup { ProjectId = service.ProjectId, ServiceId = service.Id }; db.DatabaseBackups.Add(next); await db.SaveChangesAsync(); await Invoke("ProcessBackups", db);
            await db.Entry(backup).ReloadAsync(); Assert.Equal("Expired", backup.State);
            var objects = await client.ListObjectsV2Async(new() { BucketName = bucket });
            Assert.Single(objects.S3Objects); Assert.Contains(next.Id.ToString("N"), objects.S3Objects[0].Key);
        }
        finally
        {
            try { await runtime.Docker(["rm", "-f", "-v", DatabaseRuntime.Container(service)], default); } catch { }
            try
            {
                var objects = await client.ListObjectsV2Async(new() { BucketName = bucket });
                foreach (var item in objects.S3Objects ?? []) await client.DeleteObjectAsync(bucket, item.Key);
                await client.DeleteBucketAsync(bucket);
            }
            catch (AmazonS3Exception) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
            NpgsqlConnection.ClearAllPools(); await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await command.ExecuteNonQueryAsync();
        }
    }
}
public sealed class RemoteBackupFactAttribute : FactAttribute
{
    public RemoteBackupFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORGEDOCK_TEST_S3_ENDPOINT") is null || Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock") is null)
            Skip = "Set FORGEDOCK_TEST_S3_ENDPOINT and ConnectionStrings__ForgeDock; requires local Docker with postgres:17-alpine.";
    }
}
