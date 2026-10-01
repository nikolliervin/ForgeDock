using System.Text.Json.Nodes;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed class DatabaseRuntime(
    ProcessRunner runner,
    SecretProtector protector,
    string runtimePath
)
{
    public static string Network(Guid project) => $"forgedock-db-{project:N}";

    public static string Container(DatabaseService service) => $"forgedock-db-{service.Id:N}";

    public static string Volume(DatabaseService service) => $"forgedock-db-data-{service.Id:N}";

    /// <summary>
    /// Generates a random database password with an engine-compatible complexity prefix.
    /// </summary>
    public static string NewPassword() =>
        "FdAa1!"
        + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string Variable(DatabaseKind kind) =>
        kind switch
        {
            DatabaseKind.PostgreSql => "DATABASE_URL",
            DatabaseKind.Redis => "REDIS_URL",
            DatabaseKind.MySql => "MYSQL_URL",
            DatabaseKind.SqlServer => "SQLSERVER_CONNECTION_STRING",
            DatabaseKind.MongoDb => "MONGODB_URL",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static string Image(DatabaseKind kind) =>
        kind switch
        {
            DatabaseKind.PostgreSql => "postgres:17-alpine",
            DatabaseKind.Redis => "redis:7.4-alpine",
            DatabaseKind.MySql => "mysql:8.4",
            DatabaseKind.SqlServer => "mcr.microsoft.com/mssql/server:2022-latest",
            DatabaseKind.MongoDb => "mongo:8.0",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Formats engine-specific connection values, escaping passwords in URI formats before encryption by the
    /// caller.
    /// </summary>
    public static string Connection(DatabaseService service, string password)
    {
        var host = Container(service);
        var escaped = Uri.EscapeDataString(password);
        return service.Kind switch
        {
            DatabaseKind.PostgreSql => $"postgresql://app:{escaped}@{host}:5432/app",
            DatabaseKind.Redis => $"redis://:{escaped}@{host}:6379/0",
            DatabaseKind.MySql => $"mysql://app:{escaped}@{host}:3306/app",
            DatabaseKind.SqlServer =>
                $"Server={host},1433;Database=app;User Id=app;Password={password};Encrypt=True;TrustServerCertificate=True",
            DatabaseKind.MongoDb => $"mongodb://app:{escaped}@{host}:27017/app?authSource=app",
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };
    }

    public static string DataPath(DatabaseKind kind) =>
        kind switch
        {
            DatabaseKind.PostgreSql => "/var/lib/postgresql/data",
            DatabaseKind.Redis => "/data",
            DatabaseKind.MySql => "/var/lib/mysql",
            DatabaseKind.SqlServer => "/var/opt/mssql",
            DatabaseKind.MongoDb => "/data/db",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public const string SqlCommand =
        "SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b";
    public const string MongoCommand =
        "mongosh --quiet --username forgedock --password \"$MONGO_INITDB_ROOT_PASSWORD\" --authenticationDatabase admin";

    public Task<string> Docker(IEnumerable<string> args, CancellationToken ct) =>
        runner.RunAsync(
            "docker",
            args,
            null,
            _ => Task.CompletedTask,
            ct,
            inheritEnvironment: false
        );

    /// <summary>
    /// Checks the database service label before running backup or lifecycle commands against its
    /// deterministic container name.
    /// </summary>
    public async Task AssertOwned(DatabaseService service, CancellationToken ct)
    {
        var label = await Docker(
            [
                "inspect",
                "--format",
                "{{index .Config.Labels \"io.forgedock.database\"}}",
                Container(service),
            ],
            ct
        );
        if (label != service.Id.ToString())
            throw new InvalidOperationException("Database ownership mismatch.");
    }

    /// <summary>
    /// Creates a missing database network or volume with project labels and rejects existing names owned by
    /// another project.
    /// </summary>
    private async Task EnsureResource(string type, string name, Guid project, CancellationToken ct)
    {
        var names = await Docker(
            [type, "ls", "--filter", $"name=^{name}$", "--format", "{{.Name}}"],
            ct
        );
        if (!names.Split('\n').Contains(name))
            await Docker(
                [
                    type,
                    "create",
                    "--label",
                    "io.forgedock.managed=true",
                    "--label",
                    $"io.forgedock.project={project}",
                    name,
                ],
                ct
            );
        var info = JsonNode.Parse(await Docker([type, "inspect", name], ct))!.AsArray()[0]!;
        if (info["Labels"]?["io.forgedock.project"]?.GetValue<string>() != project.ToString())
            throw new InvalidOperationException("Database resource ownership mismatch.");
    }

    /// <summary>
    /// Creates a private persistent database container, writes credentials through an owner-only file, and
    /// waits for engine readiness. Existing owned containers and data volumes are reused.
    /// </summary>
    public async Task Provision(DatabaseService service, CancellationToken ct)
    {
        await EnsureResource("network", Network(service.ProjectId), service.ProjectId, ct);
        await EnsureResource("volume", Volume(service), service.ProjectId, ct);
        var name = Container(service);
        var existing = await Docker(
            ["ps", "-a", "--filter", $"name=^{name}$", "--format", "{{.Names}}"],
            ct
        );
        if (!existing.Split('\n').Contains(name))
        {
            var image = Image(service.Kind);
            await Docker(["pull", image], ct);
            var directory = Path.Combine(Path.GetFullPath(runtimePath), "secrets");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"db-{Guid.NewGuid():N}.env");
            try
            {
                using (var stream = PrivateFiles.Create(file))
                using (var writer = new StreamWriter(stream))
                {
                    var password = protector.Unprotect(service.ProtectedPassword);
                    await writer.WriteAsync(
                        service.Kind switch
                        {
                            DatabaseKind.PostgreSql =>
                                $"POSTGRES_USER=app\nPOSTGRES_DB=app\nPOSTGRES_PASSWORD={password}\n",
                            DatabaseKind.Redis => $"REDIS_PASSWORD={password}\n",
                            DatabaseKind.MySql =>
                                $"MYSQL_DATABASE=app\nMYSQL_USER=app\nMYSQL_PASSWORD={password}\nMYSQL_ROOT_PASSWORD={password}\n",
                            DatabaseKind.SqlServer =>
                                $"ACCEPT_EULA=Y\nMSSQL_PID=Express\nMSSQL_SA_PASSWORD={password}\n",
                            DatabaseKind.MongoDb =>
                                $"MONGO_INITDB_ROOT_USERNAME=forgedock\nMONGO_INITDB_ROOT_PASSWORD={password}\n",
                            _ => throw new ArgumentOutOfRangeException(nameof(service)),
                        }
                    );
                }
                var args = new List<string>
                {
                    "create",
                    "--name",
                    name,
                    "--network",
                    Network(service.ProjectId),
                    "--restart",
                    "unless-stopped",
                    "--label",
                    "io.forgedock.managed=true",
                    "--label",
                    $"io.forgedock.project={service.ProjectId}",
                    "--label",
                    $"io.forgedock.database={service.Id}",
                    "--memory",
                    service.Kind == DatabaseKind.SqlServer ? "2048m" : "512m",
                    "--cpus",
                    "1",
                    "--pids-limit",
                    "256",
                    "--security-opt",
                    "no-new-privileges:true",
                    "--env-file",
                    file,
                    "--mount",
                    $"type=volume,src={Volume(service)},dst={DataPath(service.Kind)}",
                    image,
                };
                if (service.Kind == DatabaseKind.Redis)
                    args.AddRange([
                        "sh",
                        "-c",
                        "exec redis-server --appendonly yes --requirepass \"$REDIS_PASSWORD\"",
                    ]);
                await Docker(args, ct);
            }
            finally
            {
                File.Delete(file);
            }
        }
        await AssertOwned(service, ct);
        await Docker(["start", name], ct);
        for (var attempt = 0; attempt < 90; attempt++)
        {
            try
            {
                var command = service.Kind switch
                {
                    DatabaseKind.PostgreSql => "pg_isready -U app -d app",
                    DatabaseKind.Redis => "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli ping",
                    DatabaseKind.MySql =>
                        "MYSQL_PWD=\"$MYSQL_ROOT_PASSWORD\" mysql -u root -e 'SELECT 1' app",
                    DatabaseKind.SqlServer => SqlCommand + " -Q 'SELECT 1'",
                    DatabaseKind.MongoDb => MongoCommand + " --eval 'db.adminCommand({ping:1})'",
                    _ => throw new ArgumentOutOfRangeException(nameof(service)),
                };
                var result = await Docker(["exec", name, "sh", "-c", command], ct);
                if (service.Kind == DatabaseKind.Redis && result != "PONG")
                    throw new InvalidOperationException();
                if (service.Kind == DatabaseKind.SqlServer)
                {
                    await Docker(
                        [
                            "exec",
                            name,
                            "sh",
                            "-c",
                            SqlCommand + " -Q \"IF DB_ID('app') IS NULL CREATE DATABASE app;\"",
                        ],
                        ct
                    );
                    await Docker(
                        [
                            "exec",
                            name,
                            "sh",
                            "-c",
                            SqlCommand
                                + " -Q \"IF SUSER_ID('app') IS NULL CREATE LOGIN app WITH PASSWORD=N'$MSSQL_SA_PASSWORD'; USE app; IF USER_ID('app') IS NULL CREATE USER app FOR LOGIN app; ALTER ROLE db_owner ADD MEMBER app;\"",
                        ],
                        ct
                    );
                }
                if (service.Kind == DatabaseKind.MongoDb)
                    await Docker(
                        [
                            "exec",
                            name,
                            "sh",
                            "-c",
                            MongoCommand
                                + " --eval 'const app = db.getSiblingDB(\"app\"); if (!app.getUser(\"app\")) app.createUser({user:\"app\", pwd:process.env.MONGO_INITDB_ROOT_PASSWORD, roles:[{role:\"readWrite\",db:\"app\"}]});'",
                        ],
                        ct
                    );
                return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(2000, ct);
        }
        throw new InvalidOperationException("Database did not become ready within 180 seconds.");
    }

    /// <summary>
    /// Attaches an application/task container to the project database network only if it is not already
    /// attached.
    /// </summary>
    public async Task Connect(Guid project, string container, CancellationToken ct)
    {
        var networks = JsonNode.Parse(await Docker(["inspect", container], ct))!.AsArray()[0]![
            "NetworkSettings"
        ]!["Networks"]!.AsObject();
        if (!networks.ContainsKey(Network(project)))
            await Docker(["network", "connect", Network(project), container], ct);
    }

    /// <summary>
    /// Stops or removes only the owned database container; persistent data volumes deliberately survive
    /// project deletion.
    /// </summary>
    public async Task Stop(DatabaseService service, bool remove, CancellationToken ct)
    {
        var names = await Docker(
            ["ps", "-a", "--filter", $"name=^{Container(service)}$", "--format", "{{.Names}}"],
            ct
        );
        if (!names.Split('\n').Contains(Container(service)))
            return;
        await AssertOwned(service, ct);
        await Docker(["stop", Container(service)], ct);
        if (remove)
            await Docker(["rm", Container(service)], ct);
    }
}
