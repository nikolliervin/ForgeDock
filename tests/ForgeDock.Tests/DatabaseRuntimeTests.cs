using System.Security.Cryptography;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
namespace ForgeDock.Tests;
public class DatabaseRuntimeTests
{
    [DockerFact]
    public async Task DatabasesPersistAndArePrivate()
    {
        var project = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "forgedock-db-test-" + project);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = new SecretProtector(key);
        var runtime = new DatabaseRuntime(new ProcessRunner(), protector, root);
        var services = new[] { DatabaseKind.PostgreSql, DatabaseKind.Redis }.Select(kind => new DatabaseService { ProjectId = project, Kind = kind,
            ProtectedPassword = protector.Protect(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))) }).ToArray();
        try
        {
            foreach (var service in services)
            {
                await runtime.Provision(service, default);
                Assert.Equal("map[]", await runtime.Docker(["inspect", "--format", "{{.HostConfig.PortBindings}}", DatabaseRuntime.Container(service)], default));
                if (service.Kind == DatabaseKind.PostgreSql)
                    await runtime.Docker(["exec", DatabaseRuntime.Container(service), "psql", "-U", "app", "-d", "app", "-c", "CREATE TABLE persisted(value text); INSERT INTO persisted VALUES ('kept');"], default);
                else await runtime.Docker(["exec", DatabaseRuntime.Container(service), "sh", "-c", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli SET persisted kept"], default);
                var backups = new BackupRuntime(runtime, root, key); var backupId = Guid.NewGuid();
                Assert.True(await backups.Create(service, backupId, default) > 0);
                if (service.Kind == DatabaseKind.PostgreSql)
                    await runtime.Docker(["exec", DatabaseRuntime.Container(service), "psql", "-U", "app", "-d", "app", "-c", "UPDATE persisted SET value='changed'; CREATE TABLE extra(id int);"], default);
                else await runtime.Docker(["exec", DatabaseRuntime.Container(service), "sh", "-c", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli SET persisted changed"], default);
                await backups.Restore(service, backupId, default);
                await runtime.Stop(service, true, default); await runtime.Provision(service, default);
                var value = await runtime.Docker(service.Kind == DatabaseKind.PostgreSql
                    ? ["exec", DatabaseRuntime.Container(service), "psql", "-U", "app", "-d", "app", "-Atc", "SELECT value FROM persisted"]
                    : ["exec", DatabaseRuntime.Container(service), "sh", "-c", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli GET persisted"], default);
                Assert.Equal("kept", value);
            }
        }
        finally
        {
            foreach (var service in services) { await runtime.Stop(service, true, default); await runtime.Docker(["volume", "rm", DatabaseRuntime.Volume(service)], default); }
            await runtime.Docker(["network", "rm", DatabaseRuntime.Network(project)], default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [DockerFact]
    public Task MySqlBackupRestoresAndPersists() => VerifyAdditional(DatabaseKind.MySql);
    [DockerFact]
    public Task SqlServerBackupRestoresAndPersists() => VerifyAdditional(DatabaseKind.SqlServer);
    [DockerFact]
    public Task MongoDbBackupRestoresAndPersists() => VerifyAdditional(DatabaseKind.MongoDb);
    private static async Task VerifyAdditional(DatabaseKind kind)
    {
        var project = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "forgedock-db-test-" + project);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var protector = new SecretProtector(key);
        var runtime = new DatabaseRuntime(new ProcessRunner(), protector, root);
        var service = new DatabaseService { ProjectId = project, Kind = kind, ProtectedPassword = protector.Protect(DatabaseRuntime.NewPassword()) };
        var container = DatabaseRuntime.Container(service);
        Task<string> Query(string sql) => runtime.Docker(["exec", container, "sh", "-c", kind switch
        {
            DatabaseKind.MySql => "MYSQL_PWD=\"$MYSQL_PASSWORD\" mysql -u app -N app -e \"" + sql + "\"",
            DatabaseKind.SqlServer => "SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S localhost -U app -d app -C -b -h -1 -W -Q \"" + sql + "\"",
            _ => "mongosh --quiet --username app --password \"$MONGO_INITDB_ROOT_PASSWORD\" --authenticationDatabase app app --eval \"" + sql + "\""
        }], default);
        try
        {
            await runtime.Provision(service, default);
            Assert.Equal("map[]", await runtime.Docker(["inspect", "--format", "{{.HostConfig.PortBindings}}", container], default));
            await Query(kind == DatabaseKind.MongoDb ? "db.persisted.insertOne({value:'kept'}); db.persisted.createIndex({value:1});" : "CREATE TABLE persisted(value varchar(32)); INSERT INTO persisted VALUES ('kept');");
            var backups = new BackupRuntime(runtime, root, key); var id = Guid.NewGuid();
            Assert.True(await backups.Create(service, id, default) > 0);
            await Query(kind == DatabaseKind.MongoDb ? "db.persisted.updateMany({}, {\\$set:{value:'changed'}}); db.extra.insertOne({value:'later'});" : "UPDATE persisted SET value='changed'; CREATE TABLE extra(id int);");
            await backups.Restore(service, id, default);
            Assert.Equal("kept", (await Query(kind switch { DatabaseKind.MongoDb => "print(db.persisted.findOne().value)", DatabaseKind.MySql => "SELECT value FROM persisted", _ => "SET NOCOUNT ON; SELECT value FROM persisted" })).Trim());
            var extras = await Query(kind switch
            {
                DatabaseKind.MongoDb => "print(db.getCollectionNames().includes('extra') ? 1 : 0)",
                DatabaseKind.MySql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='app' AND table_name='extra'",
                _ => "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables WHERE name='extra'"
            });
            Assert.Equal("0", extras.Trim());
            if (kind == DatabaseKind.MongoDb) Assert.Equal("2", (await Query("print(db.persisted.getIndexes().length)")).Trim());
            await runtime.Stop(service, true, default); await runtime.Provision(service, default);
            Assert.Equal("kept", (await Query(kind switch { DatabaseKind.MongoDb => "print(db.persisted.findOne().value)", DatabaseKind.MySql => "SELECT value FROM persisted", _ => "SET NOCOUNT ON; SELECT value FROM persisted" })).Trim());
            var invalid = Guid.NewGuid(); await File.WriteAllTextAsync(backups.FilePath(invalid), "broken encrypted archive");
            await Assert.ThrowsAnyAsync<Exception>(() => backups.Restore(service, invalid, default));
            var invalidPlain = Path.Combine(root, "invalid-database-archive");
            await File.WriteAllTextAsync(invalidPlain, "invalid native archive");
            File.Delete(backups.FilePath(invalid));
            await BackupEncryption.Encrypt(invalidPlain, backups.FilePath(invalid), key, default);
            await Assert.ThrowsAnyAsync<Exception>(() => backups.Restore(service, invalid, default));
            Assert.Equal("kept", (await Query(kind switch { DatabaseKind.MongoDb => "print(db.persisted.findOne().value)", DatabaseKind.MySql => "SELECT value FROM persisted", _ => "SET NOCOUNT ON; SELECT value FROM persisted" })).Trim());
        }
        finally
        {
            await runtime.Stop(service, true, default);
            try { await runtime.Docker(["volume", "rm", DatabaseRuntime.Volume(service)], default); } catch (InvalidOperationException) { }
            try { await runtime.Docker(["network", "rm", DatabaseRuntime.Network(project)], default); } catch (InvalidOperationException) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public void DatabasePasswordsAndConnectionsSupportSqlServerAndUris()
    {
        var password = DatabaseRuntime.NewPassword();
        Assert.Contains("!", password); Assert.Contains("A", password); Assert.Contains("a", password); Assert.Contains("1", password);
        Assert.NotEqual(password, DatabaseRuntime.NewPassword());
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            var service = new DatabaseService { Kind = kind };
            var connection = DatabaseRuntime.Connection(service, password);
            Assert.Contains(DatabaseRuntime.Container(service), connection);
            if (kind != DatabaseKind.SqlServer) Assert.Contains(Uri.EscapeDataString(password), connection);
            else Assert.Contains("Password=" + password + ";", connection);
        }
        Assert.Equal(5, Enum.GetValues<DatabaseKind>().Select(DatabaseRuntime.Variable).Distinct().Count());
    }
    [Fact]
    public void ConnectionsUsePrivateServiceHosts()
    {
        var service = new DatabaseService { Kind = DatabaseKind.PostgreSql };
        Assert.Equal($"postgresql://app:secret@forgedock-db-{service.Id:N}:5432/app", DatabaseRuntime.Connection(service, "secret"));
        service.Kind = DatabaseKind.Redis;
        Assert.Equal($"redis://:secret@forgedock-db-{service.Id:N}:6379/0", DatabaseRuntime.Connection(service, "secret"));
    }
}
