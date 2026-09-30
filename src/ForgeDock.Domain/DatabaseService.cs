namespace ForgeDock.Domain;
public enum DatabaseKind { PostgreSql, Redis }
public sealed class DatabaseService
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DatabaseKind Kind { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
