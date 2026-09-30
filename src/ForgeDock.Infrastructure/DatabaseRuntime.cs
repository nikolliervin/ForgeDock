using ForgeDock.Domain;
using System.Text.Json.Nodes;

namespace ForgeDock.Infrastructure;

public sealed class DatabaseRuntime(ProcessRunner runner, SecretProtector protector, string runtimePath)
{
    public static string Network(Guid project) => $"forgedock-db-{project:N}";
    public static string Container(DatabaseService service) => $"forgedock-db-{service.Id:N}";
    public static string Volume(DatabaseService service) => $"forgedock-db-data-{service.Id:N}";
    public static string Variable(DatabaseKind kind) => kind == DatabaseKind.PostgreSql ? "DATABASE_URL" : "REDIS_URL";
    public static string Connection(DatabaseService service, string password) => service.Kind == DatabaseKind.PostgreSql
        ? $"postgresql://app:{password}@{Container(service)}:5432/app" : $"redis://:{password}@{Container(service)}:6379/0";
    public Task<string> Docker(IEnumerable<string> args, CancellationToken ct) => runner.RunAsync("docker", args, null, _ => Task.CompletedTask, ct, inheritEnvironment: false);
    public async Task AssertOwned(DatabaseService service, CancellationToken ct)
    {
        var label = await Docker(["inspect", "--format", "{{index .Config.Labels \"io.forgedock.database\"}}", Container(service)], ct);
        if (label != service.Id.ToString()) throw new InvalidOperationException("Database ownership mismatch.");
    }
    private async Task EnsureResource(string type, string name, Guid project, CancellationToken ct)
    {
        var names = await Docker([type, "ls", "--filter", $"name=^{name}$", "--format", "{{.Name}}"], ct);
        if (!names.Split('\n').Contains(name))
            await Docker([type, "create", "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={project}", name], ct);
        var info = JsonNode.Parse(await Docker([type, "inspect", name], ct))!.AsArray()[0]!;
        if (info["Labels"]?["io.forgedock.project"]?.GetValue<string>() != project.ToString()) throw new InvalidOperationException("Database resource ownership mismatch.");
    }
    public async Task Provision(DatabaseService service, CancellationToken ct)
    {
        await EnsureResource("network", Network(service.ProjectId), service.ProjectId, ct);
        await EnsureResource("volume", Volume(service), service.ProjectId, ct);
        var name = Container(service);
        var existing = await Docker(["ps", "-a", "--filter", $"name=^{name}$", "--format", "{{.Names}}"], ct);
        if (!existing.Split('\n').Contains(name))
        {
            var image = service.Kind == DatabaseKind.PostgreSql ? "postgres:17-alpine" : "redis:7.4-alpine";
            await Docker(["pull", image], ct);
            var directory = Path.Combine(Path.GetFullPath(runtimePath), "secrets"); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"db-{Guid.NewGuid():N}.env");
            try
            {
                using (var stream = PrivateFiles.Create(file))
                using (var writer = new StreamWriter(stream))
                {
                    var password = protector.Unprotect(service.ProtectedPassword);
                    await writer.WriteAsync(service.Kind == DatabaseKind.PostgreSql ? $"POSTGRES_USER=app\nPOSTGRES_DB=app\nPOSTGRES_PASSWORD={password}\n" : $"REDIS_PASSWORD={password}\n");
                }
                var args = new List<string> { "create", "--name", name, "--network", Network(service.ProjectId), "--restart", "unless-stopped",
                    "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={service.ProjectId}", "--label", $"io.forgedock.database={service.Id}",
                    "--memory", "512m", "--cpus", "1", "--pids-limit", "256", "--security-opt", "no-new-privileges:true", "--env-file", file,
                    "--mount", $"type=volume,src={Volume(service)},dst={(service.Kind == DatabaseKind.PostgreSql ? "/var/lib/postgresql/data" : "/data")}", image };
                if (service.Kind == DatabaseKind.Redis) args.AddRange(["sh", "-c", "exec redis-server --appendonly yes --requirepass \"$REDIS_PASSWORD\""]);
                await Docker(args, ct);
            }
            finally { File.Delete(file); }
        }
        await AssertOwned(service, ct); await Docker(["start", name], ct);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                var result = await Docker(service.Kind == DatabaseKind.PostgreSql ? ["exec", name, "pg_isready", "-U", "app", "-d", "app"] :
                    ["exec", name, "sh", "-c", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli ping"], ct);
                if (service.Kind != DatabaseKind.Redis || result == "PONG") return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(2000, ct);
        }
        throw new InvalidOperationException("Database did not become ready within 60 seconds.");
    }
    public async Task Connect(Guid project, string container, CancellationToken ct)
    {
        var networks = JsonNode.Parse(await Docker(["inspect", container], ct))!.AsArray()[0]!["NetworkSettings"]!["Networks"]!.AsObject();
        if (!networks.ContainsKey(Network(project))) await Docker(["network", "connect", Network(project), container], ct);
    }
    public async Task Stop(DatabaseService service, bool remove, CancellationToken ct)
    {
        var names = await Docker(["ps", "-a", "--filter", $"name=^{Container(service)}$", "--format", "{{.Names}}"], ct);
        if (!names.Split('\n').Contains(Container(service))) return;
        await AssertOwned(service, ct); await Docker(["stop", Container(service)], ct);
        if (remove) await Docker(["rm", Container(service)], ct);
    }
}
