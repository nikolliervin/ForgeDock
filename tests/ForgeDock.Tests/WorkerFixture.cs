using System.Reflection;
using System.Security.Cryptography;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using DeploymentWorker = ForgeDock.Worker.Worker;
namespace ForgeDock.Tests;

// Docker integration fixture uses unique projects/images and the local platform's existing proxy/network.
public sealed class WorkerFixture : IAsyncDisposable
{
    private readonly string database = "fd_worker_test_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection admin = null!;
    private ServiceProvider provider = null!;
    private IServiceScope scope = null!;
    public ForgeDockDbContext Db { get; private set; } = null!;
    public SecretProtector Protector { get; } = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public ProcessRunner Runner { get; } = new();
    public DeploymentWorker Worker { get; private set; } = null!;
    public string BuildRoot { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public List<Guid> Projects { get; } = [];
    public List<string> Images { get; } = [];
    public Task<string> Docker(params string[] args) => Runner.RunAsync("docker", args, null, _ => Task.CompletedTask, default, inheritEnvironment: false);
    public async Task Initialize()
    {
        // dotnet test's working directory can be tests/ForgeDock.Tests: resolve the repo's runtime explicitly.
        var repo = Directory.GetCurrentDirectory(); while (!File.Exists(Path.Combine(repo, "ForgeDock.sln"))) repo = Directory.GetParent(repo)!.FullName;
        RuntimeRoot = Path.Combine(repo, ".runtime");
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock")!;
        admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(original) { Database = "postgres" }.ConnectionString); await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await command.ExecuteNonQueryAsync();
        provider = new ServiceCollection().AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(new NpgsqlConnectionStringBuilder(original) { Database = database }.ConnectionString)).BuildServiceProvider();
        scope = provider.CreateScope(); Db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>(); await Db.Database.MigrateAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ForgeDock:RuntimePath"] = RuntimeRoot }).Build();
        Worker = new DeploymentWorker(provider.GetRequiredService<IServiceScopeFactory>(), config, Runner, Protector, NullLogger<DeploymentWorker>.Instance);
        Directory.CreateDirectory(BuildRoot);
    }
    public string RuntimeRoot { get; private set; } = "";
    public Task Invoke(string method, params object[] args) => (Task)typeof(DeploymentWorker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Worker, args)!;
    public async Task BuildImage(string image)
    {
        Images.Add(image);
        await File.WriteAllTextAsync(Path.Combine(BuildRoot, "nginx.conf"), "pid /tmp/nginx.pid; error_log /dev/stderr; events {} http { access_log /dev/stdout; client_body_temp_path /tmp/client; proxy_temp_path /tmp/proxy; fastcgi_temp_path /tmp/fastcgi; uwsgi_temp_path /tmp/uwsgi; scgi_temp_path /tmp/scgi; server { listen 8080; location / { root /www; } } }");
        await File.WriteAllTextAsync(Path.Combine(BuildRoot, "Dockerfile"), "FROM nginx:alpine\nRUN mkdir /www && chmod 777 /www\nCOPY nginx.conf /etc/nginx/nginx.conf\nUSER nginx\nCMD [\"sh\",\"-c\",\"printf '%s' \\\"$RELEASE_VALUE\\\" > /www/index.html; exec nginx -g 'daemon off;'\"]\n");
        await Docker("build", "-t", image, BuildRoot);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var project in Projects)
        {
            var containers = (await Docker("ps", "-a", "--filter", $"label=io.forgedock.project={project}", "--format", "{{.ID}}")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var container in containers) await Docker("rm", "-f", container);
            File.Delete(Path.Combine(RuntimeRoot, "routes", $"{project:N}.conf"));
        }
        await Docker("exec", "forgedock-proxy", "nginx", "-s", "reload");
        foreach (var image in Images) { try { await Docker("image", "rm", image); } catch { } }
        if (Directory.Exists(BuildRoot)) Directory.Delete(BuildRoot, true);
        scope.Dispose(); await provider.DisposeAsync(); NpgsqlConnection.ClearAllPools();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await command.ExecuteNonQueryAsync(); await admin.DisposeAsync();
    }
}
