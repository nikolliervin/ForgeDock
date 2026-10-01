using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed partial class Worker
{
    /// <summary>
    /// Runs a timed command against the verified candidate container, redacts/caps output, and persists
    /// completion even on interruption. Rollback/restart do not replay hooks; promotion uses destination
    /// hooks.
    /// </summary>
    private async Task RunDeploymentHook(
        ForgeDockDbContext db,
        Deployment deployment,
        DeploymentSnapshot snapshot,
        string phase,
        CancellationToken ct
    )
    {
        // Replaying older images must not implicitly replay schema-changing commands.
        if (deployment.RollbackSourceId != null && deployment.Trigger != "Promotion")
            return;
        var command =
            phase == "BeforeRoute" ? snapshot.PreDeployCommand : snapshot.PostDeployCommand;
        if (string.IsNullOrWhiteSpace(command))
            return;
        var hook = new DeploymentHook { DeploymentId = deployment.Id, Phase = phase };
        db.DeploymentHooks.Add(hook);
        await db.SaveChangesAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(snapshot.HookTimeoutSeconds + 15));
            var inspection = await runner.RunAsync(
                "docker",
                ["inspect", "--", deployment.ContainerId!],
                null,
                _ => Task.CompletedTask,
                timeout.Token,
                inheritEnvironment: false
            );
            var container = ContainerConsole.VerifyContainer(
                inspection,
                deployment.ProjectId,
                deployment.Id,
                snapshot.DeploymentMode == DeploymentMode.Compose,
                deployment.ImageTag
            );
            var result = await ContainerConsole.ExecuteAsync(
                container,
                TimedContainerCommand.ShellCommand(command, snapshot.HookTimeoutSeconds),
                timeout.Token
            );
            hook.Output = TimedContainerCommand.Redact(
                result.Output,
                snapshot.ProtectedEnvironment.Values.Select(protector.Unprotect),
                result.Truncated
            );
            hook.Truncated = result.Truncated || hook.Output.Length > 65536;
            if (hook.Output.Length > 65536)
                hook.Output = hook.Output[..65536];
            hook.ExitCode = result.ExitCode;
            hook.State = result.ExitCode == 0 ? "Completed" : "Failed";
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            hook.State = "Failed";
            hook.Output =
                error is OperationCanceledException
                    ? "Hook execution exceeded its timeout."
                    : "Hook could not execute. Check /bin/sh, timeout, and container ownership.";
        }
        finally
        {
            hook.FinishedAt = DateTimeOffset.UtcNow;
            if (hook.State == "Running")
            {
                hook.State = "Failed";
                hook.Output =
                    "Worker interrupted during hook execution; inspect command side effects before retrying.";
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        db.Logs.Add(
            new DeploymentLog
            {
                DeploymentId = deployment.Id,
                Phase = "Hook",
                Message =
                    $"{phase} hook {hook.State.ToLowerInvariant()} (exit {hook.ExitCode?.ToString() ?? "unavailable"}).",
            }
        );
        await db.SaveChangesAsync(ct);
        if (hook.State != "Completed")
            throw new InvalidOperationException(
                $"{phase} hook failed. Review hook output; database changes are not automatically reverted."
            );
    }
}
