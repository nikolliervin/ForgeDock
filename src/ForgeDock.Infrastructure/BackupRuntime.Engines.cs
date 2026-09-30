using ForgeDock.Domain;
namespace ForgeDock.Infrastructure;
public sealed partial class BackupRuntime
{
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private Task<string> Shell(DatabaseService service, string command, CancellationToken ct) =>
        databases.Docker(["exec", DatabaseRuntime.Container(service), "sh", "-c", "set -e; umask 077; " + command], ct);
    private Task<string> Sql(DatabaseService service, string query, CancellationToken ct) =>
        Shell(service, DatabaseRuntime.SqlCommand + " -Q " + Quote(query), ct);
    private Task<string> Mongo(DatabaseService service, string script, CancellationToken ct) =>
        Shell(service, DatabaseRuntime.MongoCommand + " --eval " + Quote(script), ct);
    private async Task Dump(DatabaseService service, string remote, CancellationToken ct)
    {
        switch (service.Kind)
        {
            case DatabaseKind.PostgreSql:
                await databases.Docker(["exec", DatabaseRuntime.Container(service), "pg_dump", "-U", "app", "-d", "app", "-Fc", "--no-owner", "--no-privileges", "-f", remote], ct); break;
            case DatabaseKind.Redis:
                await Shell(service, $"REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli --rdb {remote}", ct); break;
            case DatabaseKind.MySql:
                await Shell(service, $"MYSQL_PWD=\"$MYSQL_ROOT_PASSWORD\" mysqldump -u root --single-transaction --routines --events --triggers --no-tablespaces --set-gtid-purged=OFF app > {remote}", ct); break;
            case DatabaseKind.SqlServer:
                await Sql(service, $"BACKUP DATABASE app TO DISK=N'{remote}' WITH COPY_ONLY, INIT, CHECKSUM;", ct); break;
            case DatabaseKind.MongoDb:
                await Shell(service, $"mongodump --username forgedock --password \"$MONGO_INITDB_ROOT_PASSWORD\" --authenticationDatabase admin --db app --archive={remote} --gzip", ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(service));
        }
    }
    private async Task RestoreAdditional(DatabaseService service, string remote, CancellationToken ct)
    {
        var safety = remote + ".safety";
        // SQL Server runs as a non-root user and must be able to read the copied archive.
        if (service.Kind == DatabaseKind.SqlServer)
        {
            await databases.Docker(["exec", "--user", "0", DatabaseRuntime.Container(service), "chown", "mssql", remote], ct);
            await Shell(service, $"chmod 600 {remote}", ct);
        }
        if (service.Kind == DatabaseKind.MySql)
        {
            var staging = "restore_" + Guid.NewGuid().ToString("N");
            const string mysql = "MYSQL_PWD=\"$MYSQL_ROOT_PASSWORD\" mysql -u root";
            await Shell(service, mysql + $" -e 'CREATE DATABASE {staging}'", ct);
            try { await Shell(service, mysql + $" {staging} < {remote}", ct); }
            finally { await Shell(service, mysql + $" -e 'DROP DATABASE IF EXISTS {staging}'", CancellationToken.None); }
        }
        else if (service.Kind == DatabaseKind.SqlServer)
            await Sql(service, $"RESTORE VERIFYONLY FROM DISK=N'{remote}' WITH CHECKSUM;", ct);
        else if (service.Kind == DatabaseKind.MongoDb)
            await Shell(service, MongoRestore(remote) + " --dryRun", ct);
        else throw new ArgumentOutOfRangeException(nameof(service));

        await Dump(service, safety, ct);
        var recovered = false;
        try
        {
            try { await Replace(service, remote, ct); recovered = true; }
            catch
            {
                // Recover the previous data if a validated import fails during replacement.
                await Replace(service, safety, CancellationToken.None); recovered = true;
                throw;
            }
        }
        finally
        {
            // Keep the recovery file if both replacement and recovery fail.
            if (recovered) await Shell(service, $"rm -f {safety}", CancellationToken.None);
        }
    }
    private static string MongoRestore(string remote) =>
        $"mongorestore --username forgedock --password \"$MONGO_INITDB_ROOT_PASSWORD\" --authenticationDatabase admin --archive={remote} --gzip --nsInclude='app.*' --stopOnError";
    private async Task Replace(DatabaseService service, string remote, CancellationToken ct)
    {
        switch (service.Kind)
        {
            case DatabaseKind.MySql:
                await Shell(service, $"MYSQL_PWD=\"$MYSQL_ROOT_PASSWORD\" mysql -u root -e 'DROP DATABASE IF EXISTS app; CREATE DATABASE app'; MYSQL_PWD=\"$MYSQL_ROOT_PASSWORD\" mysql -u root app < {remote}", ct); break;
            case DatabaseKind.SqlServer:
                try
                {
                    await Sql(service, $"ALTER DATABASE app SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE app FROM DISK=N'{remote}' WITH REPLACE, RECOVERY, CHECKSUM; ALTER DATABASE app SET MULTI_USER;", ct);
                }
                finally { await Sql(service, "IF DB_ID('app') IS NOT NULL AND DATABASEPROPERTYEX('app','Status')='ONLINE' ALTER DATABASE app SET MULTI_USER;", CancellationToken.None); }
                break;
            case DatabaseKind.MongoDb:
                await Mongo(service, "db.getSiblingDB('app').dropDatabase()", ct);
                await Shell(service, MongoRestore(remote), ct);
                await databases.Provision(service, ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(service));
        }
    }
}
