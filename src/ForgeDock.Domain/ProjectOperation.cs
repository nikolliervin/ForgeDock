namespace ForgeDock.Domain;

public enum ProjectOperationKind { Stop, Delete }
public enum ProjectOperationState { Queued, Running, Completed, Failed }
public sealed class ProjectOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public ProjectOperationKind Kind { get; set; }
    public ProjectOperationState State { get; set; } = ProjectOperationState.Queued;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
