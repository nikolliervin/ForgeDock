using System.Security.Cryptography;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ForgeDock.Tests;
public class StorageCleanupTests
{
    [DockerFact]
    public async Task CleanupPreservesReferencesAndVolumesWhileRemovingEligibleArtifacts()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock")!;
        var database = "fd_storage_test_" + Guid.NewGuid().ToString("N"); var connection = new NpgsqlConnectionStringBuilder(original) { Database = database };
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(original) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync(); await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await command.ExecuteNonQueryAsync();
        var root = Path.Combine(Path.GetTempPath(), database); Directory.CreateDirectory(Path.Combine(root, "sources"));
        var runner = new ProcessRunner(); Task<string> Docker(string[] args, CancellationToken ct) => runner.RunAsync("docker", args, null, _ => Task.CompletedTask, ct, inheritEnvironment: false);
        var project = new Project { Name = "Cleanup test", RepositoryUrl = "https://github.com/example/test" };
        var old = StorageRetentionTests.Successful(project.Id, "", 20); old.ImageTag = $"forgedock/{project.Id:N}:{old.Id:N}";
        var active = StorageRetentionTests.Successful(project.Id, "", 1); active.ImageTag = $"forgedock/{project.Id:N}:{active.Id:N}"; project.ActiveDeploymentId = active.Id;
        var queued = new Deployment { ProjectId = project.Id, RollbackSourceId = old.Id, ImageTag = old.ImageTag };
        var container = "fd-cleanup-" + Guid.NewGuid().ToString("N"); var volume = container + "-data";
        await using var db = new ForgeDockDbContext(new DbContextOptionsBuilder<ForgeDockDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.MigrateAsync(); db.Projects.Add(project); db.Deployments.AddRange(old, active, queued);
            db.Logs.Add(new DeploymentLog { DeploymentId = old.Id, Timestamp = DateTimeOffset.UtcNow.AddDays(-50), Message = "old log" }); await db.SaveChangesAsync();
            foreach (var deployment in new[] { old, active }) await Docker(["tag", "postgres:17-alpine", deployment.ImageTag!], default);
            await Docker(["volume", "create", volume], default);
            await Docker(["create", "--name", container, "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={project.Id}", "--label", $"io.forgedock.deployment={old.Id}",
                "--mount", $"type=volume,src={volume},dst=/test-data", old.ImageTag!], default);
            foreach (var deployment in new[] { old, queued }) { var path = Path.Combine(root, "sources", deployment.Id.ToString("N")); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "source"), "data"); }
            var orphan = Guid.NewGuid().ToString("N"); var orphanPath = Path.Combine(root, "sources", orphan); Directory.CreateDirectory(orphanPath); Directory.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-10));
            Directory.CreateDirectory(Path.Combine(root, "backups")); await File.WriteAllTextAsync(Path.Combine(root, "backups", "keep.fgbackup"), "backup");
            Task<string> IsolatedDocker(string[] args, CancellationToken ct) => args.Length > 1 && args[0] == "image" && args[1] == "ls"
                ? Task.FromResult(old.ImageTag + "\n" + active.ImageTag) : Docker(args, ct);
            var runtime = new StorageRuntime(runner, new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), root, IsolatedDocker);
            var policy = new StoragePolicy { RetainedDeployments = 1, SourceRetentionDays = 1, OrphanRetentionDays = 1, LogRetentionDays = 1 };
            var plan = await runtime.Preview(db, policy, default); Assert.DoesNotContain(plan.Artifacts, a => a.Kind == "Image"); Assert.Contains(plan.Artifacts, a => a.Name == orphan);
            Assert.DoesNotContain(plan.Artifacts, a => a.Name == queued.Id.ToString("N"));
            queued.TransitionTo(DeploymentState.Cancelled); await db.SaveChangesAsync();
            var removed = await runtime.Cleanup(db, policy, default); Assert.Contains(removed, a => a.Kind == "Image" && a.Name == $"forgedock/{project.Id:N}:{old.Id:N}");
            Assert.Null(old.ImageTag); Assert.False(Directory.Exists(orphanPath)); Assert.True(File.Exists(Path.Combine(root, "backups", "keep.fgbackup"))); Assert.Equal(0, await db.Logs.CountAsync());
            await Docker(["volume", "inspect", volume], default); await Docker(["image", "inspect", active.ImageTag!], default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Docker(["inspect", container], default));
        }
        finally
        {
            try { await Docker(["rm", "-f", container], default); } catch { }
            await Docker(["volume", "rm", volume], default);
            foreach (var deployment in new[] { old, active }) { try { await Docker(["image", "rm", $"forgedock/{project.Id:N}:{deployment.Id:N}"], default); } catch { } }
            Directory.Delete(root, true); NpgsqlConnection.ClearAllPools(); await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await command.ExecuteNonQueryAsync();
        }
    }
}
