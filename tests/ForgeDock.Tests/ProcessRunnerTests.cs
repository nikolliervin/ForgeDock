using System.Diagnostics;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task MachineReadableOutputExcludesStderrAndPlatformSecrets()
    {
        var result = await new ProcessRunner().RunAsync(
            "/bin/sh",
            ["-c", "printf '%s' \"${ForgeDock__ApiToken-unset}\"; printf 'warning\\n' >&2"],
            null,
            _ => Task.CompletedTask,
            CancellationToken.None,
            inheritEnvironment: false
        );
        Assert.Equal("unset", result);
    }

    [Fact]
    public async Task ArgumentsArePassedLiterallyWithoutShellEvaluation()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            "/usr/bin/printf",
            ["%s", "literal; exit 7"],
            null,
            _ => Task.CompletedTask,
            CancellationToken.None
        );
        Assert.Equal("literal; exit 7", result);
    }

    [Fact]
    public async Task CapturesBothStreamsBeforeReportingFailure()
    {
        var output = new List<string>();
        var runner = new ProcessRunner();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(
                "/bin/sh",
                ["-c", "printf 'build output\\n'; printf 'build failure\\n' >&2; exit 7"],
                null,
                line =>
                {
                    output.Add(line);
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
        );
        Assert.Contains("code 7", error.Message);
        Assert.Contains("build output", output);
        Assert.Contains("build failure", output);
    }

    [Fact]
    public async Task CancellationTerminatesTheProcessTreePromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(
                "/bin/sh",
                ["-c", "sleep 30"],
                null,
                _ => Task.CompletedTask,
                cancellation.Token
            )
        );
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LogSinksReceiveCompleteSecretsBeforeApplyingStorageLimits()
    {
        var secret = new string('s', 12000);
        var lines = new List<string>();
        await new ProcessRunner().RunAsync(
            "/bin/sh",
            ["-c", "printf '%s\\n' \"$DEMO_VALUE\""],
            null,
            line =>
            {
                lines.Add(TimedContainerCommand.Redact(line, [secret]));
                return Task.CompletedTask;
            },
            CancellationToken.None,
            environment: new Dictionary<string, string> { ["DEMO_VALUE"] = secret }
        );
        Assert.Equal("[REDACTED]", Assert.Single(lines));
    }

    [Fact]
    public async Task MachineReadableOutputHasAHardCaptureLimit()
    {
        var output = await new ProcessRunner().RunAsync(
            "/bin/sh",
            ["-c", "head -c 1100000 /dev/zero | tr '\\0' x"],
            null,
            _ => Task.CompletedTask,
            CancellationToken.None
        );
        Assert.Equal(1_000_000, output.Length);
    }

    [Fact]
    public void TruncatedCaptureRedactsPartialSecretAtTheBoundary()
    {
        Assert.Equal(
            "output [REDACTED]",
            TimedContainerCommand.Redact("output private-se", ["private-secret"], truncated: true)
        );
        Assert.Equal(
            "output private-se",
            TimedContainerCommand.Redact("output private-se", ["private-secret"])
        );
    }
}
