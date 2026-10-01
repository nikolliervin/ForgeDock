namespace ForgeDock.Domain;

public sealed class PreviewEnvironment
{
    public Guid ParentProjectId { get; set; }
    public int Number { get; set; }
    public Guid? ProjectId { get; set; }
    public bool Closed { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
}
