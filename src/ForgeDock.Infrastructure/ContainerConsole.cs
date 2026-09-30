using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record ConsoleResult(string Output, int ExitCode, bool Truncated);

public static class ContainerConsole
{
    public static bool ValidCommand(string? command) => !string.IsNullOrWhiteSpace(command)
        && command.Length <= 4096 && !command.Contains('\0');

    // Resolve names to immutable Docker IDs before execution, preventing a replaced Compose
    // container from receiving a command intended for the inspected deployment.
    public static string VerifyContainer(string inspection, Guid projectId, Guid deploymentId, bool compose, string? expectedImage = null)
    {
        using var json = JsonDocument.Parse(inspection);
        var container = json.RootElement[0];
        var labels = container.GetProperty("Config").GetProperty("Labels");
        bool Label(string key, string value) => labels.TryGetProperty(key, out var label) && label.GetString() == value;
        if (!Label("io.forgedock.managed", "true") || !Label("io.forgedock.project", projectId.ToString())
            || (!compose && !Label("io.forgedock.deployment", deploymentId.ToString())))
            throw new InvalidOperationException("Container ownership does not match this project's active deployment.");
        if (!container.GetProperty("State").GetProperty("Running").GetBoolean()
            || container.GetProperty("State").GetProperty("Paused").GetBoolean())
            throw new InvalidOperationException("The application container must be running and unpaused.");
        if (expectedImage is not null && container.GetProperty("Config").GetProperty("Image").GetString() != expectedImage)
            throw new InvalidOperationException("Container image does not match the active deployment.");
        var host = container.GetProperty("HostConfig");
        if (host.GetProperty("Privileged").GetBoolean()
            || host.GetProperty("PidMode").GetString() == "host"
            || host.GetProperty("NetworkMode").GetString() == "host"
            || container.GetProperty("Mounts").EnumerateArray().Any(m => m.GetProperty("Type").GetString() == "bind"))
            throw new InvalidOperationException("Console access is unavailable for containers with host access or bind mounts.");
        var id = container.GetProperty("Id").GetString() ?? "";
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) throw new InvalidOperationException("Invalid Docker container ID.");
        return id;
    }

    public static async Task<ConsoleResult> ExecuteAsync(string containerId, string command, CancellationToken ct)
    {
        if (!ValidCommand(command) || !Regex.IsMatch(containerId, "^[a-f0-9]{64}$"))
            throw new ArgumentException("Invalid console command or container ID.");
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "exec", containerId, "/bin/sh", "-c", command }) info.ArgumentList.Add(argument);
        // Only Docker connection settings are inherited by the host-side CLI.
        info.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "XDG_RUNTIME_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start Docker.");
        process.StandardInput.Close();
        var output = new StringBuilder();
        var gate = new object();
        var truncated = false;
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
                lock (gate)
                {
                    var remaining = Math.Max(0, 65536 - output.Length);
                    output.Append(buffer, 0, Math.Min(count, remaining));
                    if (count > remaining) truncated = true;
                }
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(ct));
            return new(output.ToString(), process.ExitCode, truncated);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
