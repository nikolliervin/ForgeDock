using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeDock.Worker;

public sealed partial class Worker
{
    private ComposeRuntime ComposeEngine => new(runner, protector,
        configuration["ForgeDock:RuntimePath"] ?? ".runtime", configuration["ForgeDock:Network"] ?? "forgedock",
        configuration["ForgeDock:ComposeExecutable"]);
    private JsonObject ReadCompose(string manifest) => JsonNode.Parse(protector.Unprotect(manifest))!.AsObject();

    private async Task ExecuteComposeDeployment(ForgeDockDbContext db, Deployment deployment, Project project,
        DeploymentSnapshot snapshot, CancellationToken ct)
    {
        var errors = ForgeDock.Application.ProjectConfiguration.Validate(project.Name, snapshot.RepositoryUrl, snapshot.Branch,
            snapshot.Dockerfile, snapshot.ContainerPort, snapshot.HealthPath, snapshot.DeploymentMode, snapshot.ComposeFile, snapshot.ComposeService);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        var source = Path.Combine(Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime"), "sources", deployment.Id.ToString("N"));
        var previous = project.ActiveDeploymentId is { } id ? await db.Deployments.FindAsync([id], ct) : null;
        JsonObject? model = null;
        async Task Log(string line)
        {
            foreach (var value in snapshot.ProtectedEnvironment.Values.Select(protector.Unprotect).Where(v => v.Length > 0))
                line = line.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line, Phase = deployment.State is DeploymentState.Queued or DeploymentState.Preparing or DeploymentState.Cloning or DeploymentState.Building ? "Build" : "Runtime" }); await db.SaveChangesAsync(ct);
        }
        async Task Stage(DeploymentState state) { deployment.TransitionTo(state); await Log($"Stage: {state}"); }
        Task<string> Run(string tool, params string[] args) => runner.RunAsync(tool, args, null, Log, ct);
        await Stage(DeploymentState.Preparing);
        try
        {
            if (deployment.RollbackSourceId is null)
            {
                await Stage(DeploymentState.Cloning); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                await GitRepository.RunAsync(runner, snapshot.RepositoryUrl, configuration["ForgeDock:GitHubToken"],
                    ["-c", "http.followRedirects=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                    "clone", "--depth", "1", "--single-branch", "--branch", snapshot.Branch, "--", snapshot.RepositoryUrl, source], Log, ct);
                await ReadRevision(deployment, snapshot.RepositoryUrl, source, ct);
                await Stage(DeploymentState.Building);
                model = await ComposeEngine.PrepareAsync(source, project.Id, deployment.Id, snapshot, Log, ct);
                deployment.ProtectedComposeManifest = protector.Protect(model.ToJsonString());
                deployment.ImageTag = model["services"]![snapshot.ComposeService]!["image"]!.GetValue<string>();
            }
            else
                model = deployment.ProtectedComposeManifest is { } artifact ? ReadCompose(artifact)
                    : throw new InvalidOperationException("Retained Compose deployment manifest is missing.");
            await Stage(DeploymentState.Starting);
            project.HealthStatus = "Deploying";
            deployment.ContainerId = ComposeDefinition.ContainerName(project.Id, snapshot.ComposeService);
            await db.SaveChangesAsync(ct);
            await ComposeEngine.StartAsync(model, project.Id, Log, ct);
            await Stage(DeploymentState.HealthChecking);
            await WaitForComposeHttp(project.Id, snapshot, ct);
            deployment.ServiceStatusJson = JsonSerializer.Serialize(await ComposeEngine.StatusAsync(project.Id, ct));
            await Stage(DeploymentState.Routing);
            await RouteCompose(db, project.Id, snapshot, ct);
            project.ActiveDeploymentId = deployment.Id; project.HealthStatus = "Running";
            await Stage(DeploymentState.Running);
            if (previous is not null)
            {
                if (previous.ProtectedComposeManifest is null && previous.ContainerId is { } oldContainer)
                {
                    try
                    {
                        var label = await Run("docker", "inspect", "--format", "{{index .Config.Labels \"io.forgedock.deployment\"}}", oldContainer);
                        if (label != previous.Id.ToString()) throw new InvalidOperationException("Previous container ownership label does not match.");
                        await Run("docker", "stop", oldContainer);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { await Log($"New stack is serving; previous container cleanup needs attention: {error.Message}"); }
                }
                if (previous.State == DeploymentState.Running) previous.TransitionTo(DeploymentState.Stopped);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested && deployment.State != DeploymentState.Running)
        {
            if (model is not null)
            {
                try
                {
                    if (previous?.ProtectedComposeManifest is { } retained)
                    {
                        var old = DeploymentSnapshot.Deserialize(previous.ConfigurationJson);
                        await Log("Deployment failed; restoring the previous Compose images and configuration.");
                        await ComposeEngine.StartAsync(ReadCompose(retained), project.Id, Log, ct);
                        await WaitForComposeHttp(project.Id, old, ct);
                        await RouteCompose(db, project.Id, old, ct);
                        project.HealthStatus = "Running";
                    }
                    else
                    {
                        await ComposeEngine.StopAsync(model, project.Id, true, Log, ct);
                        project.HealthStatus = previous is null ? "NotDeployed" : "Running";
                    }
                }
                catch (Exception recovery) when (recovery is not OperationCanceledException)
                {
                    project.HealthStatus = "Unhealthy";
                    await Log($"Automatic Compose recovery failed: {recovery.Message}. Inspect services before redeploying.");
                }
                await db.SaveChangesAsync(ct);
            }
            throw;
        }
    }

    private async Task WaitForComposeHttp(Guid projectId, DeploymentSnapshot snapshot, CancellationToken ct)
    {
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await runner.RunAsync("docker", ["exec", proxy, "wget", "-q", "-T", "2", "-O", "/dev/null",
                    $"http://{ComposeDefinition.ContainerName(projectId, snapshot.ComposeService)}:{snapshot.ContainerPort}{snapshot.HealthPath}"],
                    null, _ => Task.CompletedTask, ct); return;
            }
            catch (InvalidOperationException) { await Task.Delay(2000, ct); }
        }
        throw new InvalidOperationException("Selected Compose service did not pass its HTTP health check within 60 seconds.");
    }

    private async Task RouteCompose(ForgeDockDbContext db, Guid projectId, DeploymentSnapshot snapshot, CancellationToken ct)
    {
        var root = Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime");
        var route = Path.Combine(root, "routes", $"{projectId:N}.conf"); Directory.CreateDirectory(Path.GetDirectoryName(route)!);
        var before = File.Exists(route) ? await File.ReadAllTextAsync(route, ct) : null;
        var config = ApplicationRoute.Create(projectId, ComposeDefinition.ContainerName(projectId, snapshot.ComposeService),
            snapshot.ContainerPort, await VerifiedDomains(db, projectId, ct));
        await File.WriteAllTextAsync(route + ".tmp", config, ct); File.Move(route + ".tmp", route, true);
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        try
        {
            await runner.RunAsync("docker", ["exec", proxy, "nginx", "-t"], null, _ => Task.CompletedTask, ct);
            await runner.RunAsync("docker", ["exec", proxy, "nginx", "-s", "reload"], null, _ => Task.CompletedTask, ct);
        }
        catch
        {
            if (before is null) File.Delete(route); else await File.WriteAllTextAsync(route, before, CancellationToken.None);
            throw;
        }
    }

    private async Task MonitorCompose(ForgeDockDbContext db, Project project, Deployment deployment,
        DeploymentSnapshot snapshot, CancellationToken ct)
    {
        if (deployment.State == DeploymentState.Stopped) { project.HealthStatus = "Stopped"; return; }
        try
        {
            var statuses = await ComposeEngine.StatusAsync(project.Id, ct);
            deployment.ServiceStatusJson = JsonSerializer.Serialize(statuses);
            var model = ReadCompose(deployment.ProtectedComposeManifest!);
            if (statuses.Count == 0 || statuses.All(s => s.State is "exited" or "completed")) project.HealthStatus = "Stopped";
            else if (statuses.Count != model["services"]!.AsObject().Count || statuses.Any(s => s.State is not ("running" or "completed") || s.Health is "unhealthy" or "starting"))
                project.HealthStatus = "Unhealthy";
            else { await WaitForComposeHttp(project.Id, snapshot, ct); project.HealthStatus = "Running"; }
            await ComposeEngine.LogsAsync(model, project.Id, async line =>
            {
                db.Logs.Add(new DeploymentLog { DeploymentId = deployment.Id, Message = line, Phase = "Runtime" }); await db.SaveChangesAsync(ct);
            }, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { project.HealthStatus = "Unhealthy"; logger.LogWarning(error, "Compose health check failed for {ProjectId}", project.Id); }
    }
}
