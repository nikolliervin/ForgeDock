using System.Diagnostics;
using System.Text;

namespace ForgeDock.Infrastructure;

public static class BoundedDockerProcess
{
    /// <summary>
    /// Runs a Docker command with closed stdin and a minimal host environment. Drains both streams even
    /// after the 64 KiB character limit to avoid pipe deadlocks. Cancellation kills the host CLI; callers
    /// must separately terminate container work when required.
    /// </summary>
    public static async Task<ConsoleResult> Run(string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (
            var name in new[]
            {
                "PATH",
                "HOME",
                "DOCKER_HOST",
                "DOCKER_CONTEXT",
                "DOCKER_CONFIG",
                "XDG_RUNTIME_DIR",
            }
        )
            if (Environment.GetEnvironmentVariable(name) is { } value)
                info.Environment[name] = value;
        using var process =
            Process.Start(info) ?? throw new InvalidOperationException("Docker is unavailable.");
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
                    if (count > remaining)
                        truncated = true;
                }
        }
        try
        {
            await Task.WhenAll(
                Drain(process.StandardOutput),
                Drain(process.StandardError),
                process.WaitForExitAsync(ct)
            );
            return new(output.ToString(), process.ExitCode, truncated);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
