namespace ForgeDock.Domain;

public enum DeploymentState { Queued, Preparing, Cloning, Building, Starting, HealthChecking, Routing, Running, Failed, Stopped, Cancelled }

public sealed class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string ConfigurationJson { get; set; } = "";
    public DeploymentState State { get; private set; } = DeploymentState.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DeploymentState LastStage { get; private set; } = DeploymentState.Queued;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? RequestedCommit { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }
    public string? CommitSha { get; set; }
    public string? ImageTag { get; set; }
    public string? ProtectedComposeManifest { get; set; }
    public string ServiceStatusJson { get; set; } = "[]";
    public string? ContainerId { get; set; }
    public string? Error { get; private set; }
    public Guid? RollbackSourceId { get; set; }

    public void TransitionTo(DeploymentState next, string? error = null)
    {
        var allowed = (State, next) switch
        {
            (DeploymentState.Queued, DeploymentState.Preparing) => true,
            (DeploymentState.Queued, DeploymentState.Cancelled) => true,
            (DeploymentState.Preparing, DeploymentState.Cloning) => true,
            (DeploymentState.Preparing, DeploymentState.Starting) when RollbackSourceId.HasValue => true,
            (DeploymentState.Cloning, DeploymentState.Building) => true,
            (DeploymentState.Building, DeploymentState.Starting) => true,
            (DeploymentState.Starting, DeploymentState.HealthChecking) => true,
            (DeploymentState.HealthChecking, DeploymentState.Routing) => true,
            (DeploymentState.Routing, DeploymentState.Running) => true,
            (DeploymentState.Running, DeploymentState.Stopped) => true,
            (_, DeploymentState.Failed) when State is not (DeploymentState.Failed or DeploymentState.Stopped or DeploymentState.Cancelled) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Invalid deployment transition: {State} → {next}.");
        if (next == DeploymentState.Failed && string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("A failed deployment requires an actionable error.", nameof(error));
        if (next == DeploymentState.Preparing) StartedAt = DateTimeOffset.UtcNow;
        LastStage = next is DeploymentState.Failed or DeploymentState.Cancelled ? State : next;
        if (next is DeploymentState.Running or DeploymentState.Failed or DeploymentState.Cancelled)
            FinishedAt = DateTimeOffset.UtcNow;
        State = next;
        Error = next == DeploymentState.Failed ? error : null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
