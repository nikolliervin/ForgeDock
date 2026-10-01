namespace ForgeDock.Domain;

public sealed class DatabaseBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public string Kind { get; set; } = "Backup";
    public Guid? SourceBackupId { get; set; }
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }
    public string RemoteState { get; set; } = "Disabled";
    public string? RemoteEndpoint { get; set; }
    public string? RemoteRegion { get; set; }
    public string? RemoteBucket { get; set; }
    public string? RemoteKey { get; set; }
    public DateTimeOffset? RemoteNextAttemptAt { get; set; }
    public string? RemoteError { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
