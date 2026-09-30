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
        var protector = new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
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
    [Fact]
    public void ConnectionsUsePrivateServiceHosts()
    {
        var service = new DatabaseService { Kind = DatabaseKind.PostgreSql };
        Assert.Equal($"postgresql://app:secret@forgedock-db-{service.Id:N}:5432/app", DatabaseRuntime.Connection(service, "secret"));
        service.Kind = DatabaseKind.Redis;
        Assert.Equal($"redis://:secret@forgedock-db-{service.Id:N}:6379/0", DatabaseRuntime.Connection(service, "secret"));
    }
}
