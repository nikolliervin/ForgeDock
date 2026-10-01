namespace ForgeDock.Domain;

public sealed class StoragePolicy
{
    public int Id { get; set; } = 1;
    public bool AutomaticCleanup { get; set; }
    public int RetainedDeployments { get; set; } = 5;
    public int SourceRetentionDays { get; set; } = 7;
    public int LogRetentionDays { get; set; } = 30;
    public int OrphanRetentionDays { get; set; } = 7;
    public DateTimeOffset? LastCleanupAt { get; set; }
}

public sealed class StorageCleanup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string State { get; set; } = "Queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public string ResultJson { get; set; } = "[]";
}
