using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public static class AutomaticRollback
{
    /// <summary>
    /// Starts the opt-in release observation window and records the previous release. Rollback/restart
    /// replays do not recursively arm another window.
    /// </summary>
    public static void Arm(Deployment deployment, Guid? previous, DeploymentSnapshot snapshot)
    {
        if (
            !snapshot.AutoRollbackEnabled
            || previous is null
            || (deployment.RollbackSourceId != null && deployment.Trigger != "Promotion")
        )
            return;
        deployment.PreviousDeploymentId = previous;
        deployment.RollbackDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(
            snapshot.RollbackWindowMinutes
        );
        deployment.HealthFailureCount = 0;
    }

    /// <summary>
    /// Tracks consecutive failures only during an armed window and resets the count on success. A persisted
    /// trigger timestamp prevents repeated recovery queues.
    /// </summary>
    public static bool Observe(
        Deployment deployment,
        bool healthy,
        int threshold,
        DateTimeOffset now
    )
    {
        if (
            deployment.State != DeploymentState.Running
            || deployment.PreviousDeploymentId is null
            || deployment.RollbackDeadlineAt is null
            || deployment.RollbackDeadlineAt <= now
            || deployment.AutoRollbackTriggeredAt is not null
        )
            return false;
        deployment.HealthFailureCount = healthy ? 0 : deployment.HealthFailureCount + 1;
        return deployment.HealthFailureCount >= threshold;
    }
}
