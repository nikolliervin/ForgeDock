using ForgeDock.Domain;
namespace ForgeDock.Infrastructure;
public sealed class BackupRuntime(DatabaseRuntime databases, string runtimePath, string secretKey)
{
    private string Root => Path.Combine(Path.GetFullPath(runtimePath), "backups");
    public string FilePath(Guid id) => Path.Combine(Root, id.ToString("N") + ".fgbackup");
    public async Task<long> Create(DatabaseService service, Guid id, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plain = Path.Combine(Root, id.ToString("N") + ".tmp"); var remote = $"/tmp/forgedock-{id:N}";
        await databases.AssertOwned(service, ct);
        try
        {
            if (service.Kind == DatabaseKind.PostgreSql)
                await databases.Docker(["exec", DatabaseRuntime.Container(service), "pg_dump", "-U", "app", "-d", "app", "-Fc", "--no-owner", "--no-privileges", "-f", remote], ct);
            else await databases.Docker(["exec", DatabaseRuntime.Container(service), "sh", "-c", $"REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli --rdb {remote}"], ct);
            await databases.Docker(["cp", DatabaseRuntime.Container(service) + ":" + remote, plain], ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await BackupEncryption.Encrypt(plain, FilePath(id), secretKey, ct); return new FileInfo(FilePath(id)).Length;
        }
        catch { File.Delete(FilePath(id)); throw; }
        finally
        {
            File.Delete(plain);
            try { await databases.Docker(["exec", DatabaseRuntime.Container(service), "rm", "-f", remote], CancellationToken.None); } catch { }
        }
    }
    public async Task Restore(DatabaseService service, Guid backup, CancellationToken ct)
    {
        var plain = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".restore");
        var remote = $"/tmp/forgedock-restore-{Guid.NewGuid():N}"; var container = DatabaseRuntime.Container(service);
        await databases.AssertOwned(service, ct);
        try
        {
            // Authenticate the entire file before touching the database.
            await BackupEncryption.Decrypt(FilePath(backup), plain, secretKey, ct);
            await databases.Docker(["cp", plain, container + ":" + remote], ct);
            if (service.Kind == DatabaseKind.PostgreSql)
            {
                var staging = "restore_" + Guid.NewGuid().ToString("N"); var previous = "previous_" + Guid.NewGuid().ToString("N");
                await databases.Docker(["exec", container, "pg_restore", "--list", remote], ct);
                await databases.Docker(["exec", container, "createdb", "-U", "app", staging], ct);
                try
                {
                    await databases.Docker(["exec", container, "pg_restore", "-U", "app", "-d", staging, "--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction", remote], ct);
                    await databases.Docker(["exec", container, "psql", "-U", "app", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c",
                        $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='app';"], ct);
                    await databases.Docker(["exec", container, "psql", "-U", "app", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c",
                        $"BEGIN; ALTER DATABASE app RENAME TO {previous}; ALTER DATABASE {staging} RENAME TO app; COMMIT;"], ct);
                    await databases.Docker(["exec", container, "dropdb", "-U", "app", "--if-exists", previous], ct);
                }
                finally { await databases.Docker(["exec", container, "dropdb", "-U", "app", "--if-exists", staging], CancellationToken.None); }
            }
            else
            {
                await databases.Docker(["exec", container, "redis-check-rdb", remote], ct);
                await databases.Stop(service, false, ct);
                try
                {
                    await databases.Docker(["run", "--rm", "--network", "none", "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={service.ProjectId}",
                        "--mount", $"type=volume,src={DatabaseRuntime.Volume(service)},dst=/data", "--volume", $"{plain}:/restore.rdb:ro,Z", "redis:7.4-alpine",
                        "sh", "-c", "set -e; rm -rf /data/appendonlydir; cp /restore.rdb /data/dump.rdb; redis-server --dir /data --daemonize yes --appendonly no --save ''; redis-cli CONFIG SET appendonly yes; i=0; while redis-cli INFO persistence | tr -d '\\r' | grep -q 'aof_rewrite_in_progress:1'; do i=$((i+1)); test $i -lt 120; sleep 1; done; redis-cli INFO persistence | tr -d '\\r' | grep -q 'aof_last_bgrewrite_status:ok'; redis-cli SHUTDOWN NOSAVE; chown -R redis:redis /data"], ct);
                }
                finally { await databases.Docker(["start", container], CancellationToken.None); }
                await databases.Provision(service, ct);
            }
        }
        finally
        {
            File.Delete(plain);
            try { await databases.Docker(["exec", container, "rm", "-f", remote], CancellationToken.None); } catch { }
        }
    }
}
