using System.Diagnostics;
using System.Text;

namespace ForgeDock.Infrastructure;

public sealed class ProcessRunner
{
    public async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        string? directory, Func<string, Task> log, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory ?? Environment.CurrentDirectory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {executable}.");
        var output = new StringBuilder();
        var outputLock = new SemaphoreSlim(1);
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                await outputLock.WaitAsync(timeout.Token);
                try
                {
                    if (output.Length < 1_000_000) output.AppendLine(line);
                    await log(line.Length > 8000 ? line[..8000] : line);
                }
                finally { outputLock.Release(); }
            }
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(timeout.Token));
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
