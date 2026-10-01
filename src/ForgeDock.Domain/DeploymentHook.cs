namespace ForgeDock.Domain;
public sealed class DeploymentHook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeploymentId { get; set; }
    public string Phase { get; set; } = "BeforeRoute";
    public string State { get; set; } = "Running";
    public int? ExitCode { get; set; }
    public string Output { get; set; } = "";
    public bool Truncated { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
