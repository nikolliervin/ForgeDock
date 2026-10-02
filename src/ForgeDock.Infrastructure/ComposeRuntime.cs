using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeDock.Infrastructure;

public sealed class ComposeRuntime(
    ProcessRunner runner,
    SecretProtector protector,
    string runtimePath,
    string network = "forgedock",
    string? executable = null
)
{
    private readonly string root = Path.GetFullPath(runtimePath);
    private string Binary => executable ?? Path.Combine(root, "tools", "docker-compose");

    private Task<string> Docker(string[] args, CancellationToken ct) =>
        runner.RunAsync(
            "docker",
            args,
            null,
            _ => Task.CompletedTask,
            ct,
            inheritEnvironment: false
        );

    /// <summary>
    /// Invokes the installed Compose binary or Docker plugin with literal arguments and explicit project,
    /// manifest, and environment paths.
    /// </summary>
    private Task<string> Compose(
        string file,
        string environmentFile,
        string stack,
        string[] args,
        Func<string, Task> log,
        CancellationToken ct,
        string? source = null
    )
    {
        var prefix = new List<string>
        {
            "--ansi",
            "never",
            "--progress",
            "plain",
            "--project-name",
            stack,
            "--env-file",
            environmentFile,
            "--file",
            file,
        };
        if (source is not null)
        {
            prefix.Add("--project-directory");
            prefix.Add(source);
        }
        prefix.AddRange(args);
        return File.Exists(Binary)
            ? runner.RunAsync(Binary, prefix, null, log, ct, inheritEnvironment: false)
            : runner.RunAsync(
                "docker",
                new[] { "compose" }.Concat(prefix),
                null,
                log,
                ct,
                inheritEnvironment: false
            );
    }

    /// <summary>
    /// Validates before resolving env files, builds or pulls services, tags exact image IDs, and returns a
    /// replayable manifest with builds removed. Temporary plaintext manifests and environment files are
    /// deleted on every exit.
    /// </summary>
    public async Task<JsonObject> PrepareAsync(
        string source,
        Guid projectId,
        Guid deploymentId,
        DeploymentSnapshot snapshot,
        Func<string, Task> log,
        CancellationToken ct
    )
    {
        var file = ComposeDefinition.RepositoryPath(source, snapshot.ComposeFile);
        if (!File.Exists(file))
            throw new InvalidOperationException(
                "Configured Compose file is missing from the repository."
            );
        var env = string.Join(
            '\n',
            snapshot.ProtectedEnvironment.Select(pair =>
                $"{pair.Key}='{protector.Unprotect(pair.Value).Replace("'", "\\'")}'"
            )
        );
        var environment = await PrivateFile(env, ct);
        string? manifest = null;
        try
        {
            var preview = await Compose(
                file,
                environment,
                ComposeDefinition.StackName(projectId),
                ["config", "--format", "json", "--no-env-resolution"],
                _ => Task.CompletedTask,
                ct
            );
            var preflight = RepositoryConfigurationInspector.InspectCompose(
                source,
                preview,
                [],
                snapshot.ComposeFile,
                snapshot.ComposeService,
                snapshot.ContainerPort
            );
            foreach (var issue in preflight.Issues)
                await log($"Configuration {issue.Severity}: {issue.Message}");
            var invalid = preflight.Issues.Where(i => i.Severity == "error").ToArray();
            if (invalid.Length > 0)
                throw new InvalidOperationException(
                    string.Join(" ", invalid.Select(i => i.Message))
                );
            ComposeDefinition.Normalize(
                preview,
                source,
                projectId,
                deploymentId,
                snapshot.ComposeService,
                network
            );
            var config = await Compose(
                file,
                environment,
                ComposeDefinition.StackName(projectId),
                ["config", "--format", "json"],
                _ => Task.CompletedTask,
                ct
            );
            var model = ComposeDefinition.Normalize(
                config,
                source,
                projectId,
                deploymentId,
                snapshot.ComposeService,
                network
            );
            ResourceLimits.ApplyCompose(model, snapshot);
            await AssertOwnershipAsync(model, projectId, ct);
            var safeLog = RedactedLog(model, log);
            manifest = await PrivateFile(model.ToJsonString(), ct);
            await Compose(
                manifest,
                environment,
                ComposeDefinition.StackName(projectId),
                ["pull", "--ignore-buildable", "--policy", "missing"],
                safeLog,
                ct
            );
            if (model["services"]!.AsObject().Any(s => s.Value!["build"] is not null))
                await Compose(
                    manifest,
                    environment,
                    ComposeDefinition.StackName(projectId),
                    ["build"],
                    safeLog,
                    ct
                );
            foreach (var (serviceName, node) in model["services"]!.AsObject())
            {
                var service = node!.AsObject();
                var image = service["image"]!.GetValue<string>();
                var id = await Docker(["image", "inspect", "--format", "{{.Id}}", image], ct);
                var retained = ComposeDefinition.ImageName(projectId, deploymentId, serviceName);
                await Docker(["tag", id, retained], ct);
                service["image"] = retained;
                // Retained manifests replay images, never source builds.
                service.Remove("build");
            }
            return model;
        }
        finally
        {
            File.Delete(environment);
            if (manifest is not null)
                File.Delete(manifest);
        }
    }

    /// <summary>
    /// Starts only existing retained images after ownership checks; never rebuilds or pulls during replay.
    /// </summary>
    public async Task StartAsync(
        JsonObject model,
        Guid projectId,
        Func<string, Task> log,
        CancellationToken ct
    )
    {
        await AssertOwnershipAsync(model, projectId, ct);
        foreach (var (_, service) in model["services"]!.AsObject())
            await Docker(["image", "inspect", service!["image"]!.GetValue<string>()], ct);
        await WithManifest(
            model,
            projectId,
            [
                "up",
                "--detach",
                "--force-recreate",
                "--wait",
                "--wait-timeout",
                "180",
                "--no-build",
                "--pull",
                "never",
                "--remove-orphans",
            ],
            log,
            ct
        );
    }

    /// <summary>
    /// Stops or removes the owned stack while preserving named volumes. Removal also cleans owned
    /// non-database networks.
    /// </summary>
    public async Task StopAsync(
        JsonObject model,
        Guid projectId,
        bool remove,
        Func<string, Task> log,
        CancellationToken ct
    )
    {
        await AssertOwnershipAsync(model, projectId, ct);
        await WithManifest(
            model,
            projectId,
            remove ? ["down", "--remove-orphans"] : ["stop"],
            log,
            ct
        );
        if (remove)
        {
            var names = await Docker(
                [
                    "network",
                    "ls",
                    "--filter",
                    $"label=io.forgedock.project={projectId}",
                    "--format",
                    "{{.Name}}",
                ],
                ct
            );
            foreach (var name in names.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == DatabaseRuntime.Network(projectId))
                    continue;
                var info = JsonNode
                    .Parse(await Docker(["network", "inspect", name], ct))!
                    .AsArray()[0]!;
                CheckLabels(info["Labels"], projectId);
                await Docker(["network", "rm", name], ct);
            }
        }
    }

    /// <summary>
    /// Collects a bounded recent Compose log window; overlapping polls and downtime can duplicate or omit
    /// lines.
    /// </summary>
    public Task LogsAsync(
        JsonObject model,
        Guid projectId,
        Func<string, Task> log,
        CancellationToken ct
    ) =>
        WithManifest(
            model,
            projectId,
            ["logs", "--no-color", "--since", "30s", "--tail", "500"],
            log,
            ct
        );

    /// <summary>
    /// Materializes the retained manifest in a private file, applies secret-redacting log output, and
    /// removes temporary files in finally blocks.
    /// </summary>
    private async Task WithManifest(
        JsonObject model,
        Guid projectId,
        string[] args,
        Func<string, Task> log,
        CancellationToken ct
    )
    {
        var manifest = await PrivateFile(model.ToJsonString(), ct);
        string? environment = null;
        try
        {
            environment = await PrivateFile("", ct);
            await Compose(
                manifest,
                environment,
                ComposeDefinition.StackName(projectId),
                args,
                RedactedLog(model, log),
                ct
            );
        }
        finally
        {
            File.Delete(manifest);
            if (environment is not null)
                File.Delete(environment);
        }
    }

    /// <summary>
    /// Builds a log sink that masks resolved service environment values before forwarding output.
    /// </summary>
    private static Func<string, Task> RedactedLog(JsonObject model, Func<string, Task> log)
    {
        var secrets = model["services"]!
            .AsObject()
            .SelectMany(s =>
                s.Value?["environment"] is JsonObject values
                    ? values.Select(v => v.Value?.ToString().Replace("$$", "$"))
                    : []
            )
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .Distinct()
            .OrderByDescending(v => v.Length)
            .ToArray();
        return line => log(TimedContainerCommand.Redact(line, secrets));
    }

    /// <summary>Captures bounded health-check output before recovery removes failed containers.</summary>
    public async Task LogFailureDiagnosticsAsync(
        JsonObject model,
        Guid projectId,
        Func<string, Task> log,
        string failure,
        CancellationToken ct
    )
    {
        var safeLog = RedactedLog(model, log);
        await safeLog($"Deployment failed: {failure}");
        var ids = await Docker(
            [
                "ps",
                "--all",
                "--filter",
                $"label=com.docker.compose.project={ComposeDefinition.StackName(projectId)}",
                "--format",
                "{{.ID}}",
            ],
            ct
        );
        foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var container = JsonNode.Parse(await Docker(["inspect", id], ct))!.AsArray()[0]!;
            CheckLabels(container["Config"]?["Labels"], projectId);
            var service =
                container["Config"]?["Labels"]?["com.docker.compose.service"]?.ToString()
                ?? "service";
            if (
                container["State"]?["Health"] is JsonObject health
                && health["Status"]?.ToString() == "unhealthy"
            )
            {
                await safeLog($"{service}: unhealthy. Recent health-check results:");
                foreach (var check in (health["Log"] as JsonArray ?? []).TakeLast(3))
                {
                    var output = check?["Output"]?.ToString() ?? "No output";
                    await safeLog(
                        $"{service}: health check exit {check?["ExitCode"]}: {output.Trim()}"
                    );
                    if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                        await safeLog(
                            $"{service}: check script permissions and SELinux labels on mounted repository files."
                        );
                }
            }
            if (container["State"]?["Status"]?.ToString() is "exited" or "dead")
            {
                await safeLog(
                    $"{service}: container exited with code {container["State"]?["ExitCode"]}."
                );
                await runner.RunAsync(
                    "docker",
                    ["logs", "--tail", "30", id],
                    null,
                    safeLog,
                    ct,
                    inheritEnvironment: false
                );
            }
        }
    }

    /// <summary>
    /// Enumerates only this project stack, verifies ownership, and treats a zero-exit task service as
    /// completed rather than crashed.
    /// </summary>
    public async Task<IReadOnlyList<ServiceStatus>> StatusAsync(
        Guid projectId,
        CancellationToken ct
    )
    {
        var ids = await Docker(
            [
                "ps",
                "--all",
                "--filter",
                $"label=com.docker.compose.project={ComposeDefinition.StackName(projectId)}",
                "--format",
                "{{.ID}}",
            ],
            ct
        );
        var statuses = new List<ServiceStatus>();
        foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var container = JsonNode.Parse(await Docker(["inspect", id], ct))!.AsArray()[0]!;
            CheckLabels(container["Config"]?["Labels"], projectId);
            var state = container["State"]?["Status"]?.GetValue<string>() ?? "unknown";
            if (state == "exited" && container["State"]?["ExitCode"]?.GetValue<int>() == 0)
                state = "completed";
            statuses.Add(
                new ServiceStatus(
                    container["Config"]!["Labels"]![
                        "com.docker.compose.service"
                    ]!.GetValue<string>(),
                    state,
                    container["State"]?["Health"]?["Status"]?.GetValue<string>(),
                    container["Config"]!["Image"]!.GetValue<string>()
                )
            );
        }
        return statuses.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Checks existing stack containers and declared local resources before Compose can modify them.
    /// </summary>
    private async Task AssertOwnershipAsync(JsonObject model, Guid projectId, CancellationToken ct)
    {
        await StatusAsync(projectId, ct);
        foreach (var type in new[] { "volumes", "networks" })
            if (model[type] is JsonObject resources)
                foreach (var (_, resource) in resources)
                {
                    if (resource?["external"]?.GetValue<bool>() == true)
                        continue;
                    var name = resource!["name"]!.GetValue<string>();
                    var kind = type == "volumes" ? "volume" : "network";
                    var existing = await Docker(
                        [kind, "ls", "--filter", $"name={name}", "--format", "{{.Name}}"],
                        ct
                    );
                    if (!existing.Split('\n').Contains(name))
                        continue;
                    var info = JsonNode.Parse(await Docker([kind, "inspect", name], ct))!.AsArray()[
                        0
                    ]!;
                    CheckLabels(info["Labels"], projectId);
                }
    }

    /// <summary>
    /// Refuses resource modification unless both management and project ownership labels match.
    /// </summary>
    private static void CheckLabels(JsonNode? labels, Guid projectId)
    {
        if (
            labels?["io.forgedock.managed"]?.GetValue<string>() != "true"
            || labels?["io.forgedock.project"]?.GetValue<string>() != projectId.ToString()
        )
            throw new InvalidOperationException(
                "Compose resource ownership mismatch; refusing to modify it."
            );
    }

    /// <summary>
    /// Creates an exclusive owner-only temporary manifest on Linux and removes partial files if writing
    /// fails.
    /// </summary>
    private async Task<string> PrivateFile(string content, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Compose deployment requires Linux.");
        var directory = Path.Combine(root, "secrets");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".compose");
        try
        {
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }
            );
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content.AsMemory(), ct);
            return path;
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            throw;
        }
    }
}
