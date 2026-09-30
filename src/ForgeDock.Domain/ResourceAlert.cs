namespace ForgeDock.Domain;
public sealed class ResourceAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid DeploymentId { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ResourceObservation
{
    public Guid ProjectId { get; set; }
    public Guid DeploymentId { get; set; }
    public int RestartBaseline { get; set; }
    public int ExitedChecks { get; set; }
    public bool OomObserved { get; set; }
}
