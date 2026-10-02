using System.Text.RegularExpressions;
using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;

namespace ForgeDock.Api;

public record ConfigurationCheckRequest(
    string RepositoryUrl,
    string Branch = "main",
    DeploymentMode DeploymentMode = DeploymentMode.Auto,
    string RootDirectory = ".",
    string Dockerfile = "Dockerfile",
    string ComposeFile = "docker-compose.yml",
    string ComposeService = "",
    int ContainerPort = 8080,
    bool DetectDefaultBranch = false
);

public static class ConfigurationCheckEndpoints
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void MapConfigurationCheckEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(
            "/configuration/check",
            async (
                ConfigurationCheckRequest request,
                IConfiguration configuration,
                CancellationToken ct
            ) =>
            {
                var errors = ProjectConfiguration.Validate(
                    "Configuration check",
                    request.RepositoryUrl ?? "",
                    request.Branch ?? "",
                    request.Dockerfile ?? "",
                    request.ContainerPort,
                    "/",
                    request.DeploymentMode,
                    request.ComposeFile ?? "",
                    string.IsNullOrEmpty(request.ComposeService)
                        ? "discovery"
                        : request.ComposeService,
                    rootDirectory: request.RootDirectory ?? ""
                );
                if (errors.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", errors) });
                if (!await Gate.WaitAsync(0, ct))
                    return Results.Problem(
                        "Another configuration check is running. Try again shortly.",
                        statusCode: 429
                    );
                var root = Path.Combine(
                    Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime"),
                    "checks",
                    Guid.NewGuid().ToString("N")
                );
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                    var runner = new ProcessRunner();
                    var branch = request.Branch!;
                    if (request.DetectDefaultBranch)
                    {
                        var reference = await GitRepository.RunAsync(
                            runner,
                            request.RepositoryUrl!,
                            configuration["ForgeDock:GitHubToken"],
                            [
                                "-c",
                                "http.followRedirects=false",
                                "-c",
                                "protocol.allow=never",
                                "-c",
                                "protocol.https.allow=always",
                                "ls-remote",
                                "--symref",
                                "--",
                                request.RepositoryUrl!,
                                "HEAD",
                            ],
                            _ => Task.CompletedTask,
                            timeout.Token
                        );
                        var match = Regex.Match(
                            reference,
                            @"(?m)^ref: refs/heads/([^\t\r\n]+)\tHEAD"
                        );
                        if (!match.Success)
                            return Results.Problem(
                                "Could not find the repository's default branch. Check repository access or enter a branch explicitly.",
                                statusCode: 422
                            );
                        branch = match.Groups[1].Value;
                    }
                    if (!request.DetectDefaultBranch)
                    {
                        var reference = await GitRepository.RunAsync(
                            runner,
                            request.RepositoryUrl!,
                            configuration["ForgeDock:GitHubToken"],
                            [
                                "-c",
                                "http.followRedirects=false",
                                "-c",
                                "protocol.allow=never",
                                "-c",
                                "protocol.https.allow=always",
                                "ls-remote",
                                "--heads",
                                "--",
                                request.RepositoryUrl!,
                                $"refs/heads/{branch}",
                            ],
                            _ => Task.CompletedTask,
                            timeout.Token
                        );
                        if (string.IsNullOrWhiteSpace(reference))
                            return Results.Problem(
                                $"Branch '{branch}' was not found in this repository. Enter its default branch or another existing branch.",
                                statusCode: 422
                            );
                    }
                    await GitRepository.RunAsync(
                        runner,
                        request.RepositoryUrl!,
                        configuration["ForgeDock:GitHubToken"],
                        [
                            "-c",
                            "http.followRedirects=false",
                            "-c",
                            "protocol.allow=never",
                            "-c",
                            "protocol.https.allow=always",
                            "clone",
                            "--depth",
                            "1",
                            "--single-branch",
                            "--branch",
                            branch,
                            "--",
                            request.RepositoryUrl!,
                            root,
                        ],
                        _ => Task.CompletedTask,
                        timeout.Token
                    );
                    var files = RepositoryConfigurationInspector.FindComposeFiles(root);
                    var recommendCompose =
                        request.DeploymentMode == DeploymentMode.Auto && files.Length > 0;
                    if (
                        request.DeploymentMode != DeploymentMode.Compose
                        && (!recommendCompose || files.Length != 1)
                    )
                    {
                        var single = RepositoryConfigurationInspector.InspectSingle(
                            root,
                            request.DeploymentMode,
                            request.RootDirectory!,
                            request.Dockerfile!,
                            request.ContainerPort
                        );
                        return Results.Ok(
                            single with
                            {
                                SuggestedMode = recommendCompose ? "Compose" : null,
                                SuggestedBranch = branch != request.Branch ? branch : null,
                            }
                        );
                    }
                    var selected = recommendCompose ? files[0] : request.ComposeFile!;
                    var issues = new List<ConfigurationIssue>();
                    if (branch != request.Branch)
                        issues.Add(
                            new(
                                "warning",
                                $"The repository's default branch is '{branch}'. Apply this branch before deploying, or enter the branch you want."
                            )
                        );
                    var requestedPath = ComposeDefinition.RepositoryPath(root, selected);
                    if (File.Exists(requestedPath) && !files.Contains(selected))
                        files = files.Append(selected).Order().ToArray();
                    if (!File.Exists(requestedPath))
                    {
                        issues.Add(new("error", $"Compose file '{selected}' was not found."));
                        if (files.Length != 1)
                            return Results.Ok(
                                new ConfigurationCheck(files, null, [], issues.ToArray())
                            );
                        selected = files[0];
                        issues.Add(
                            new("info", $"Found '{selected}'. Apply this file and check again.")
                        );
                    }
                    var path = ComposeDefinition.RepositoryPath(root, selected);
                    // Avoid external includes/extends reading host files during inspection.
                    if (
                        Regex.IsMatch(
                            await File.ReadAllTextAsync(path, timeout.Token),
                            @"(?m)(?:^|[,{])\s*[""']?(include|extends)[""']?\s*:"
                        )
                    )
                        return Results.Ok(
                            new ConfigurationCheck(
                                files,
                                selected,
                                [],
                                [
                                    new(
                                        "warning",
                                        "Compose includes/extends require manual validation; automatic inspection is unavailable for this file."
                                    ),
                                ]
                            )
                        );
                    var binary =
                        configuration["ForgeDock:ComposeExecutable"]
                        ?? Path.Combine(
                            Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime"),
                            "tools",
                            "docker-compose"
                        );
                    var environmentFile = Path.Combine(
                        Path.GetDirectoryName(root)!,
                        $"{Path.GetFileName(root)}.env"
                    );
                    await File.WriteAllTextAsync(environmentFile, "", timeout.Token);
                    try
                    {
                        var arguments = new[]
                        {
                            "--project-name",
                            "forgedock-check",
                            "--env-file",
                            environmentFile,
                            "--file",
                            path,
                            "config",
                            "--format",
                            "json",
                            "--no-env-resolution",
                            "--no-interpolate",
                            "--no-normalize",
                            "--no-consistency",
                        };
                        var json = await runner.RunAsync(
                            File.Exists(binary) ? binary : "docker",
                            File.Exists(binary) ? arguments : new[] { "compose" }.Concat(arguments),
                            root,
                            _ => Task.CompletedTask,
                            timeout.Token,
                            inheritEnvironment: false
                        );
                        var check = RepositoryConfigurationInspector.InspectCompose(
                            root,
                            json,
                            files,
                            selected,
                            request.ComposeService!,
                            request.ContainerPort
                        );
                        return Results.Ok(
                            check with
                            {
                                Issues = issues.Concat(check.Issues).ToArray(),
                                SuggestedMode = recommendCompose ? "Compose" : null,
                                SuggestedBranch = branch != request.Branch ? branch : null,
                            }
                        );
                    }
                    finally
                    {
                        File.Delete(environmentFile);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.Problem(
                        "Repository inspection timed out. Check the repository URL and branch.",
                        statusCode: 408
                    );
                }
                catch (Exception error)
                    when (error
                            is InvalidOperationException
                                or IOException
                                or System.Text.Json.JsonException
                    )
                {
                    return Results.Problem(
                        "Could not inspect the repository. Verify repository access, branch, Compose syntax, and build paths. No build was started.",
                        statusCode: 422
                    );
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(root))
                            Directory.Delete(root, true);
                    }
                    finally
                    {
                        Gate.Release();
                    }
                }
            }
        );
    }
}
