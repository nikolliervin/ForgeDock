using System.Diagnostics;
using System.Text;

namespace ForgeDock.Infrastructure;

public sealed class ProcessRunner
{
    /// <summary>
    /// Runs literal arguments without a host shell, serializes both output streams for scoped log sinks, and
    /// captures only stdout up to one million characters. Defaults to a minimal environment; explicit
    /// overrides are for trusted callers. Cancellation or the 15-minute deadline terminates the process
    /// tree. Sinks must redact complete lines before truncation.
    /// </summary>
    public async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? directory,
        Func<string, Task> log,
        CancellationToken cancellationToken,
        bool inheritEnvironment = false,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        if (!inheritEnvironment)
        {
            var allowed = new[]
            {
                "PATH",
                "HOME",
                "DOCKER_HOST",
                "DOCKER_CONTEXT",
                "DOCKER_CONFIG",
                "XDG_RUNTIME_DIR",
            }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            info.Environment.Clear();
            foreach (var (name, value) in allowed)
                if (value is not null)
                    info.Environment[name] = value;
        }
        if (environment is not null)
            foreach (var (name, value) in environment)
                info.Environment[name] = value;
        info.Environment.Remove("ForgeDock__GitHubToken");
        info.Environment.Remove("ForgeDock__BackupS3__AccessKey");
        info.Environment.Remove("ForgeDock__BackupS3__SecretKey");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process =
            Process.Start(info)
            ?? throw new InvalidOperationException($"Unable to start {executable}.");
        var output = new StringBuilder();
        var outputLock = new SemaphoreSlim(1);
        async Task Drain(StreamReader reader, bool capture)
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                await outputLock.WaitAsync(timeout.Token);
                try
                {
                    if (capture && output.Length < 1_000_000)
                    {
                        var remaining = 1_000_000 - output.Length;
                        output.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                        if (output.Length < 1_000_000)
                            output.AppendLine();
                    }
                    // Sinks redact complete secrets before applying their own storage limit.
                    await log(line);
                }
                finally
                {
                    outputLock.Release();
                }
            }
        }
        try
        {
            await Task.WhenAll(
                Drain(process.StandardOutput, true),
                Drain(process.StandardError, false),
                process.WaitForExitAsync(timeout.Token)
            );
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{executable} exited with code {process.ExitCode}. See deployment logs."
                );
            return output.ToString().Trim();
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            outputLock.Dispose();
        }
    }
}
