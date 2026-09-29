using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ForgeDock.Worker;

public sealed partial class Worker(IServiceScopeFactory scopes, IConfiguration configuration,
    ProcessRunner runner, SecretProtector protector, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A session lock prevents a second worker from recovering or executing the same queue.
        await using var owner = new NpgsqlConnection(configuration.GetConnectionString("ForgeDock"));
        await owner.OpenAsync(stoppingToken);
        await using var ownership = new NpgsqlCommand("SELECT pg_try_advisory_lock(74623019)", owner);
        if (!(bool)(await ownership.ExecuteScalarAsync(stoppingToken))!)
            throw new InvalidOperationException("Another ForgeDock worker owns the deployment queue.");
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
            var interrupted = await db.Deployments.Where(d => d.State != DeploymentState.Queued &&
                d.State != DeploymentState.Running && d.State != DeploymentState.Stopped && d.State != DeploymentState.Failed).ToListAsync(stoppingToken);
            foreach (var deployment in interrupted)
                deployment.TransitionTo(DeploymentState.Failed, "Worker interrupted during deployment. Inspect managed resources and redeploy.");
            foreach (var operation in await db.Operations.Where(o => o.State == ProjectOperationState.Running).ToListAsync(stoppingToken))
            {
                operation.State = ProjectOperationState.Failed;
                operation.Error = "Worker interrupted during project operation. Inspect resources before retrying.";
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        var nextMonitor = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var heartbeat = new NpgsqlCommand("SELECT 1", owner);
            await heartbeat.ExecuteScalarAsync(stoppingToken);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
            if (DateTimeOffset.UtcNow >= nextMonitor)
            {
                await MonitorApplications(db, stoppingToken);
                nextMonitor = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            var operation = await db.Operations.OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(o => o.State == ProjectOperationState.Queued, stoppingToken);
            if (operation is not null)
            {
                try { await ExecuteOperation(db, operation, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    operation.State = ProjectOperationState.Failed; operation.Error = error.Message;
                    await db.SaveChangesAsync(stoppingToken);
                    logger.LogError(error, "Project operation {OperationId} failed", operation.Id);
                }
                continue;
            }
            var deployment = await db.Deployments.OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(d => d.State == DeploymentState.Queued, stoppingToken);
            if (deployment is null) { await Task.Delay(1000, stoppingToken); continue; }
            try { await ExecuteDeployment(db, deployment, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Deployment {DeploymentId} failed", deployment.Id);
                deployment.TransitionTo(DeploymentState.Failed, error.Message);
                await db.SaveChangesAsync(stoppingToken);
            }
        }
    }

    private async Task ExecuteOperation(ForgeDockDbContext db, ProjectOperation operation, CancellationToken ct)
    {
        operation.State = ProjectOperationState.Running; await db.SaveChangesAsync(ct);
        var project = await db.Projects.FindAsync([operation.ProjectId], ct)
            ?? throw new InvalidOperationException("Project no longer exists.");
        var root = Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime");
        var route = Path.Combine(root, "routes", $"{project.Id:N}.conf");
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        Task<string> Run(params string[] args) => runner.RunAsync("docker", args, null, _ => Task.CompletedTask, ct);
        var previous = File.Exists(route) ? await File.ReadAllTextAsync(route, ct) : null;
        if (previous is not null) File.Delete(route);
        try { await Run("exec", proxy, "nginx", "-t"); await Run("exec", proxy, "nginx", "-s", "reload"); }
        catch { if (previous is not null) await File.WriteAllTextAsync(route, previous, CancellationToken.None); throw; }
        var deployments = await db.Deployments.Where(d => d.ProjectId == project.Id).ToListAsync(ct);
        var compose = operation.Kind == ProjectOperationKind.Delete
            ? deployments.OrderByDescending(d => d.CreatedAt).FirstOrDefault(d => d.ProtectedComposeManifest != null)
            : deployments.FirstOrDefault(d => d.Id == project.ActiveDeploymentId && d.ProtectedComposeManifest != null);
        if (compose?.ProtectedComposeManifest is { } protectedManifest)
            await ComposeEngine.StopAsync(ReadCompose(protectedManifest), project.Id, operation.Kind == ProjectOperationKind.Delete,
                _ => Task.CompletedTask, ct);
        foreach (var deployment in deployments.Where(d => d.ContainerId != null &&
            (operation.Kind == ProjectOperationKind.Delete || d.Id == project.ActiveDeploymentId)))
        {
            if (DeploymentSnapshot.Deserialize(deployment.ConfigurationJson).DeploymentMode == DeploymentMode.Compose)
            {
                if (deployment.State == DeploymentState.Running) deployment.TransitionTo(DeploymentState.Stopped);
                deployment.ServiceStatusJson = System.Text.Json.JsonSerializer.Serialize(
                    System.Text.Json.JsonSerializer.Deserialize<List<ServiceStatus>>(deployment.ServiceStatusJson)!.Select(s => s with { State = "stopped" }));
                continue;
            }
            var name = deployment.ContainerId!;
            var existing = await Run("ps", "-a", "--filter", $"name=^{name}$", "--format", "{{.Names}}");
            if (!existing.Split('\n').Contains(name)) continue;
            var label = await Run("inspect", "--format", "{{index .Config.Labels \"io.forgedock.deployment\"}}", name);
            if (label != deployment.Id.ToString()) throw new InvalidOperationException("Container ownership label mismatch; refusing operation.");
            await Run("stop", name);
            if (operation.Kind == ProjectOperationKind.Delete) await Run("rm", name);
            if (deployment.State == DeploymentState.Running) deployment.TransitionTo(DeploymentState.Stopped);
        }
        project.HealthStatus = "Stopped";
        if (operation.Kind == ProjectOperationKind.Delete) db.Projects.Remove(project);
        operation.State = ProjectOperationState.Completed;
        await db.SaveChangesAsync(ct);
    }

    private async Task MonitorApplications(ForgeDockDbContext db, CancellationToken ct)
    {
        var projects = await db.Projects.Where(p => p.ActiveDeploymentId != null).ToListAsync(ct);
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        foreach (var project in projects)
        {
            var deployment = await db.Deployments.FindAsync([project.ActiveDeploymentId!.Value], ct);
            if (deployment?.ContainerId is not { } container) continue;
            var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
            if (snapshot.DeploymentMode == DeploymentMode.Compose)
            {
                await MonitorCompose(db, project, deployment, snapshot, ct);
                continue;
            }
            async Task Log(string line)
            {
                foreach (var secret in snapshot.ProtectedEnvironment.Values.Select(protector.Unprotect).Where(v => v.Length > 0))
                    line = line.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line });
                await db.SaveChangesAsync(ct);
            }
            try
            {
                var state = await runner.RunAsync("docker", ["inspect", "--format", "{{.State.Running}}", container], null, _ => Task.CompletedTask, ct);
                if (state != "true") { project.HealthStatus = "Stopped"; continue; }
                await runner.RunAsync("docker", ["exec", proxy, "wget", "-q", "-T", "2", "-O", "/dev/null",
                    $"http://{container}:{snapshot.ContainerPort}{snapshot.HealthPath}"], null, _ => Task.CompletedTask, ct);
                project.HealthStatus = "Running";
                await runner.RunAsync("docker", ["logs", "--since", "30s", "--tail", "500", container], null, Log, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                project.HealthStatus = "Unhealthy";
                logger.LogWarning(error, "Health check failed for {ProjectId}", project.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ExecuteDeployment(ForgeDockDbContext db, Deployment deployment, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The deployment worker requires Linux.");
        var project = await db.Projects.SingleAsync(p => p.Id == deployment.ProjectId, ct);
        var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
        if (snapshot.DeploymentMode == DeploymentMode.Compose)
        {
            await ExecuteComposeDeployment(db, deployment, project, snapshot, ct);
            return;
        }
        var validation = ForgeDock.Application.ProjectConfiguration.Validate(project.Name, snapshot.RepositoryUrl,
            snapshot.Branch, snapshot.Dockerfile, snapshot.ContainerPort, snapshot.HealthPath);
        if (validation.Count > 0) throw new InvalidOperationException(string.Join(" ", validation));
        var root = Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime");
        var source = Path.Combine(root, "sources", deployment.Id.ToString("N"));
        var network = configuration["ForgeDock:Network"] ?? "forgedock";
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        var container = $"forgedock-{deployment.Id:N}";
        async Task Log(string line)
        {
            foreach (var value in snapshot.ProtectedEnvironment.Values.Select(protector.Unprotect).Where(v => v.Length > 0))
                line = line.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line });
            await db.SaveChangesAsync(ct);
        }
        async Task Stage(DeploymentState state)
        {
            deployment.TransitionTo(state);
            await Log($"Stage: {state}");
        }
        Task<string> Run(string executable, params string[] args) => runner.RunAsync(executable, args, null, Log, ct);
        await Stage(DeploymentState.Preparing);
        if (deployment.RollbackSourceId is null)
        {
            await Stage(DeploymentState.Cloning);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            // Redirects and alternate Git protocols are disabled; operators must also restrict worker egress.
            await Run("git", "-c", "http.followRedirects=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                "clone", "--depth", "1", "--single-branch", "--branch", snapshot.Branch, "--", snapshot.RepositoryUrl, source);
            deployment.CommitSha = await Run("git", "-C", source, "rev-parse", "HEAD");
            var dockerfile = Path.Combine(source, snapshot.Dockerfile);
            if (!File.Exists(dockerfile)) throw new InvalidOperationException("Configured Dockerfile is missing from the repository.");
            var current = new FileInfo(dockerfile) as FileSystemInfo;
            while (current is not null && current.FullName != source)
            {
                if (current.LinkTarget is not null) throw new InvalidOperationException("Dockerfile path must not contain symbolic links.");
                current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
            }
            await Stage(DeploymentState.Building);
            deployment.ImageTag = $"forgedock/{project.Id:N}:{deployment.Id:N}";
            await Run("docker", "build", "--label", "io.forgedock.managed=true", "--label", $"io.forgedock.project={project.Id}",
                "-t", deployment.ImageTag, "-f", dockerfile, source);
        }
        await Stage(DeploymentState.Starting);
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        var environmentFile = Path.Combine(root, "secrets", deployment.Id.ToString("N") + ".env");
        try
        {
            using (var stream = new FileStream(environmentFile, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            using (var writer = new StreamWriter(stream))
                foreach (var (name, value) in snapshot.ProtectedEnvironment)
                    await writer.WriteLineAsync($"{name}={protector.Unprotect(value)}");
            await Run("docker", "create", "--name", container, "--network", network, "--label", "io.forgedock.managed=true",
                "--label", $"io.forgedock.project={project.Id}", "--label", $"io.forgedock.deployment={deployment.Id}",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--memory", "512m", "--cpus", "1", "--pids-limit", "256",
                "--env-file", environmentFile, deployment.ImageTag!);
        }
        finally { if (File.Exists(environmentFile)) File.Delete(environmentFile); }
        deployment.ContainerId = container;
        await db.SaveChangesAsync(ct);
        await Run("docker", "start", container);
        await Stage(DeploymentState.HealthChecking);
        var healthy = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var state = await Run("docker", "inspect", "--format", "{{.State.Running}}", container);
            if (state != "true") throw new InvalidOperationException("Application container exited before becoming healthy.");
            try
            {
                await Run("docker", "exec", proxy, "wget", "-q", "-T", "2", "-O", "/dev/null",
                    $"http://{container}:{snapshot.ContainerPort}{snapshot.HealthPath}");
                healthy = true; break;
            }
            catch (InvalidOperationException) { await Task.Delay(2000, ct); }
        }
        if (!healthy) throw new InvalidOperationException("Application did not pass HTTP health checks within 60 seconds.");
        await Stage(DeploymentState.Routing);
        var routes = Path.Combine(root, "routes");
        Directory.CreateDirectory(routes);
        var route = Path.Combine(routes, $"{project.Id:N}.conf");
        var previous = File.Exists(route) ? await File.ReadAllTextAsync(route, ct) : null;
        var content = $"server {{ listen 80; server_name {project.Id:N}.localhost; location / {{ proxy_pass http://{container}:{snapshot.ContainerPort}; proxy_set_header Host $http_host; proxy_set_header X-Forwarded-Host $http_host; proxy_set_header X-Forwarded-Proto $scheme; proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; }} }}";
        await File.WriteAllTextAsync(route + ".tmp", content, ct);
        File.Move(route + ".tmp", route, overwrite: true);
        try
        {
            await Run("docker", "exec", proxy, "nginx", "-t");
            await Run("docker", "exec", proxy, "nginx", "-s", "reload");
        }
        catch
        {
            if (previous is null) File.Delete(route);
            else await File.WriteAllTextAsync(route, previous, CancellationToken.None);
            throw;
        }
        var oldId = project.ActiveDeploymentId;
        project.ActiveDeploymentId = deployment.Id;
        project.HealthStatus = "Running";
        await Stage(DeploymentState.Running);
        await db.SaveChangesAsync(ct);
        if (oldId is { } id && await db.Deployments.FindAsync([id], ct) is { } old && old.ContainerId is { } oldContainer)
        {
            try
            {
                if (old.ProtectedComposeManifest is { } oldManifest)
                {
                    await ComposeEngine.StopAsync(ReadCompose(oldManifest), project.Id, false, Log, ct);
                    if (old.State == DeploymentState.Running) old.TransitionTo(DeploymentState.Stopped);
                    await db.SaveChangesAsync(ct);
                    return;
                }
                var label = await Run("docker", "inspect", "--format", "{{index .Config.Labels \"io.forgedock.deployment\"}}", oldContainer);
                if (label != old.Id.ToString()) throw new InvalidOperationException("Old container ownership label does not match.");
                await Run("docker", "stop", oldContainer);
                if (old.State == DeploymentState.Running) old.TransitionTo(DeploymentState.Stopped);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { await Log($"New deployment is serving; old container cleanup needs attention: {error.Message}"); }
        }
    }
}
