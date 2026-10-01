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
        using (var backupStore = CreateBackupStore()) { } // Fail fast for incomplete remote storage configuration.
        // A session lock prevents a second worker from recovering or executing the same queue.
        await using var owner = new NpgsqlConnection(configuration.GetConnectionString("ForgeDock"));
        await owner.OpenAsync(stoppingToken);
        await using var ownership = new NpgsqlCommand("SELECT pg_try_advisory_lock(74623019)", owner);
        if (!(bool)(await ownership.ExecuteScalarAsync(stoppingToken))!)
            throw new InvalidOperationException("Another ForgeDock worker owns the deployment queue.");
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
            foreach (var backup in await db.DatabaseBackups.Where(b => b.State == "Running").ToListAsync(stoppingToken))
            { backup.State = "Failed"; backup.Error = "Worker interrupted. Inspect the database and restart the app before retrying a restore."; backup.FinishedAt = DateTimeOffset.UtcNow; }
            var interrupted = await db.Deployments.Where(d => d.State != DeploymentState.Queued &&
                d.State != DeploymentState.Running && d.State != DeploymentState.Stopped && d.State != DeploymentState.Failed && d.State != DeploymentState.Cancelled).ToListAsync(stoppingToken);
            foreach (var deployment in interrupted)
                deployment.TransitionTo(DeploymentState.Failed, "Worker interrupted during deployment. Inspect managed resources and redeploy.");
            foreach (var operation in await db.Operations.Where(o => o.State == ProjectOperationState.Running).ToListAsync(stoppingToken))
            {
                operation.State = ProjectOperationState.Failed;
                operation.Error = "Worker interrupted during project operation. Inspect resources before retrying.";
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        var nextDomains = DateTimeOffset.MinValue;
        var nextMonitor = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var heartbeat = new NpgsqlCommand("SELECT 1", owner);
            await heartbeat.ExecuteScalarAsync(stoppingToken);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
            if (DateTimeOffset.UtcNow >= nextDomains)
            {
                await ReconcileDomains(db, stoppingToken);
                nextDomains = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            if (DateTimeOffset.UtcNow >= nextMonitor)
            {
                await MonitorApplications(db, stoppingToken);
                nextMonitor = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            await ProvisionDatabases(db, stoppingToken);
            await ProcessBackups(db, stoppingToken);
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
            catch (DbUpdateConcurrencyException)
            {
                // A queued cancellation may win while this worker is claiming the job.
                // SaveChanges is transactional, so neither its stage nor its log was saved.
                db.ChangeTracker.Clear();
                logger.LogInformation("Deployment {DeploymentId} changed before the worker claimed it", deployment.Id);
            }
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
        foreach (var service in await db.DatabaseServices.Where(s => s.ProjectId == project.Id).ToListAsync(ct))
        {
            await Databases.Stop(service, operation.Kind == ProjectOperationKind.Delete, ct); service.State = "Stopped";
        }
        project.HealthStatus = "Stopped";
        if (operation.Kind == ProjectOperationKind.Delete)
        {
            foreach (var preview in await db.Projects.Where(p => p.ParentProjectId == project.Id).ToListAsync(ct))
                if (!await db.Operations.AnyAsync(o => o.ProjectId == preview.Id && o.Kind == ProjectOperationKind.Delete && (o.State == ProjectOperationState.Queued || o.State == ProjectOperationState.Running), ct))
                    db.Operations.Add(new ProjectOperation { ProjectId = preview.Id, Kind = ProjectOperationKind.Delete });
            foreach (var registration in await db.PreviewEnvironments.Where(p => p.ProjectId == project.Id).ToListAsync(ct)) registration.ProjectId = null;
            db.Projects.Remove(project);
        }
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
                db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line, Phase = "Runtime" });
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

    private async Task ReadRevision(Deployment deployment, string repositoryUrl, string source, CancellationToken ct)
    {
        Task<string> Git(params string[] args) => GitRepository.RunAsync(runner, repositoryUrl, configuration["ForgeDock:GitHubToken"], args, _ => Task.CompletedTask, ct);
        if (deployment.RequestedCommit is { } commit)
        {
            if (!ForgeDock.Application.ProjectConfiguration.IsCommitSha(commit))
                throw new InvalidOperationException("Requested commit must be a full Git SHA.");
            await Git("-c", "http.followRedirects=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                "-C", source, "fetch", "--depth", "1", "origin", commit);
            await Git("-C", source, "checkout", "--detach", commit);
        }
        deployment.CommitSha = await Git("-C", source, "rev-parse", "HEAD");
        if (deployment.RequestedCommit is { } requested && !string.Equals(requested, deployment.CommitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fetched revision does not match the requested commit.");
        var message = await Git("-C", source, "log", "-1", "--format=%s");
        var author = await Git("-C", source, "log", "-1", "--format=%an");
        deployment.CommitMessage = message[..Math.Min(message.Length, 500)];
        deployment.CommitAuthor = author[..Math.Min(author.Length, 200)];
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
            snapshot.Branch, snapshot.Dockerfile, snapshot.ContainerPort, snapshot.HealthPath, snapshot.DeploymentMode,
            snapshot.ComposeFile, snapshot.ComposeService, snapshot.BuildCommand, snapshot.StartCommand, snapshot.RootDirectory);
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
            db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line, Phase = deployment.State is DeploymentState.Queued or DeploymentState.Preparing or DeploymentState.Cloning or DeploymentState.Building ? "Build" : "Runtime" });
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
            await GitRepository.RunAsync(runner, snapshot.RepositoryUrl, configuration["ForgeDock:GitHubToken"],
                    ["-c", "http.followRedirects=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                "clone", "--depth", "1", "--single-branch", "--branch", snapshot.Branch, "--", snapshot.RepositoryUrl, source], Log, ct);
            await ReadRevision(deployment, snapshot.RepositoryUrl, source, ct);
            await Stage(DeploymentState.Building);
            deployment.ImageTag = $"forgedock/{project.Id:N}:{deployment.Id:N}";
            var railpack = configuration["ForgeDock:RailpackPath"] ?? Path.Combine(root, "tools", "railpack");
            var buildkit = configuration["ForgeDock:BuildKitHost"] ?? "docker-container://forgedock-buildkit";
            await new SingleApplicationBuilder(runner).BuildAsync(snapshot, source, deployment.ImageTag,
                project.Id, railpack, buildkit, Log, ct,
                snapshot.ProtectedEnvironment.ToDictionary(e => e.Key, e => protector.Unprotect(e.Value)));
        }
        await Stage(DeploymentState.Starting);
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        var environmentFile = Path.Combine(root, "secrets", deployment.Id.ToString("N") + ".env");
        try
        {
            using (var stream = new FileStream(environmentFile, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            using (var writer = new StreamWriter(stream))
            {
                if (snapshot.DeploymentMode == DeploymentMode.Auto && !snapshot.ProtectedEnvironment.ContainsKey("PORT"))
                    await writer.WriteLineAsync($"PORT={snapshot.ContainerPort}");
                foreach (var (name, value) in snapshot.ProtectedEnvironment)
                    await writer.WriteLineAsync($"{name}={protector.Unprotect(value)}");
            }
            await runner.RunAsync("docker", ["create", "--name", container, "--network", network, "--label", "io.forgedock.managed=true",
                "--label", $"io.forgedock.project={project.Id}", "--label", $"io.forgedock.deployment={deployment.Id}",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--pids-limit", "256",
                "--env-file", environmentFile, .. ResourceLimits.DockerArguments(snapshot.CpuLimit, snapshot.MemoryLimitMiB), deployment.ImageTag!], null, Log, ct);
        }
        finally { if (File.Exists(environmentFile)) File.Delete(environmentFile); }
        deployment.ContainerId = container;
        await db.SaveChangesAsync(ct);
        await AttachDatabases(db, project.Id, container, ct);
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
        var content = ApplicationRoute.Create(project.Id, container, snapshot.ContainerPort, await VerifiedDomains(db, project.Id, ct));
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
