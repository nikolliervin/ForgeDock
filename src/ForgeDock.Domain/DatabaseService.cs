namespace ForgeDock.Domain;

public enum DatabaseKind
{
    PostgreSql,
    Redis,
    MySql,
    SqlServer,
    MongoDb,
}

public sealed class DatabaseService
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DatabaseKind Kind { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }
    public int BackupIntervalHours { get; set; }
    public DateTimeOffset? NextBackupAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
