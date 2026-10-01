using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record ConsoleResult(string Output, int ExitCode, bool Truncated);

public static class ContainerConsole
{
    public static bool ValidCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) && command.Length <= 4096 && !command.Contains('\0');

    // Resolve names to immutable Docker IDs before execution, preventing a replaced Compose
    // container from receiving a command intended for the inspected deployment.
    /// <summary>
    /// Verifies project/deployment labels, running state, image identity, and absence of dangerous host
    /// access, then returns an immutable Docker ID to prevent name-replacement races.
    /// </summary>
    public static string VerifyContainer(
        string inspection,
        Guid projectId,
        Guid deploymentId,
        bool compose,
        string? expectedImage = null
    )
    {
        using var json = JsonDocument.Parse(inspection);
        var container = json.RootElement[0];
        var labels = container.GetProperty("Config").GetProperty("Labels");
        bool Label(string key, string value) =>
            labels.TryGetProperty(key, out var label) && label.GetString() == value;
        if (
            !Label("io.forgedock.managed", "true")
            || !Label("io.forgedock.project", projectId.ToString())
            || (!compose && !Label("io.forgedock.deployment", deploymentId.ToString()))
        )
            throw new InvalidOperationException(
                "Container ownership does not match this project's active deployment."
            );
        if (
            !container.GetProperty("State").GetProperty("Running").GetBoolean()
            || container.GetProperty("State").GetProperty("Paused").GetBoolean()
        )
            throw new InvalidOperationException(
                "The application container must be running and unpaused."
            );
        if (
            expectedImage is not null
            && container.GetProperty("Config").GetProperty("Image").GetString() != expectedImage
        )
            throw new InvalidOperationException(
                "Container image does not match the active deployment."
            );
        var host = container.GetProperty("HostConfig");
        if (
            host.GetProperty("Privileged").GetBoolean()
            || host.GetProperty("PidMode").GetString() == "host"
            || host.GetProperty("NetworkMode").GetString() == "host"
            || container
                .GetProperty("Mounts")
                .EnumerateArray()
                .Any(m => m.GetProperty("Type").GetString() == "bind")
        )
            throw new InvalidOperationException(
                "Console access is unavailable for containers with host access or bind mounts."
            );
        var id = container.GetProperty("Id").GetString() ?? "";
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$"))
            throw new InvalidOperationException("Invalid Docker container ID.");
        return id;
    }

    /// <summary>
    /// Executes an operator command only inside a validated immutable container. Output is bounded;
    /// cancelling the host Docker CLI does not guarantee termination of the in-container command.
    /// </summary>
    public static async Task<ConsoleResult> ExecuteAsync(
        string containerId,
        string command,
        CancellationToken ct
    )
    {
        if (!ValidCommand(command) || !Regex.IsMatch(containerId, "^[a-f0-9]{64}$"))
            throw new ArgumentException("Invalid console command or container ID.");
        return await BoundedDockerProcess.Run(["exec", containerId, "/bin/sh", "-c", command], ct);
    }
}
