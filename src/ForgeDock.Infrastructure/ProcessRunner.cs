using System.Diagnostics;
using System.Text;

namespace ForgeDock.Infrastructure;

public sealed class ProcessRunner
{
    public async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        string? directory, Func<string, Task> log, CancellationToken cancellationToken, bool inheritEnvironment = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory ?? Environment.CurrentDirectory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (!inheritEnvironment)
        {
            var allowed = new[] { "PATH", "HOME", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "XDG_RUNTIME_DIR" }
                .ToDictionary(name => name, Environment.GetEnvironmentVariable);
            info.Environment.Clear();
            foreach (var (name, value) in allowed) if (value is not null) info.Environment[name] = value;
        }
        if (environment is not null)
            foreach (var (name, value) in environment) info.Environment[name] = value;
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {executable}.");
        var output = new StringBuilder();
        var outputLock = new SemaphoreSlim(1);
        async Task Drain(StreamReader reader, bool capture)
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                await outputLock.WaitAsync(timeout.Token);
                try
                {
                    if (capture && output.Length < 1_000_000) output.AppendLine(line);
                    await log(line.Length > 8000 ? line[..8000] : line);
                }
                finally { outputLock.Release(); }
            }
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput, true), Drain(process.StandardError, false), process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited with code {process.ExitCode}. See deployment logs.");
            return output.ToString().Trim();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { outputLock.Dispose(); }
    }
}
