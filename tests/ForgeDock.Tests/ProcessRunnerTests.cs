using ForgeDock.Infrastructure;
using System.Diagnostics;

namespace ForgeDock.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task MachineReadableOutputExcludesStderrAndPlatformSecrets()
    {
        var result = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "printf '%s' \"${ForgeDock__ApiToken-unset}\"; printf 'warning\\n' >&2"],
            null, _ => Task.CompletedTask, CancellationToken.None, inheritEnvironment: false);
        Assert.Equal("unset", result);
    }

    [Fact]
    public async Task ArgumentsArePassedLiterallyWithoutShellEvaluation()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("/usr/bin/printf", ["%s", "literal; exit 7"], null,
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("literal; exit 7", result);
    }

    [Fact]
    public async Task CapturesBothStreamsBeforeReportingFailure()
    {
        var output = new List<string>();
        var runner = new ProcessRunner();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("/bin/sh",
            ["-c", "printf 'build output\\n'; printf 'build failure\\n' >&2; exit 7"], null,
            line => { output.Add(line); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Contains("code 7", error.Message);
        Assert.Contains("build output", output);
        Assert.Contains("build failure", output);
    }

    [Fact]
    public async Task CancellationTerminatesTheProcessTreePromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync("/bin/sh",
            ["-c", "sleep 30"], null, _ => Task.CompletedTask, cancellation.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }
}
