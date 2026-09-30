namespace ForgeDock.Domain;

public sealed class NotificationSettings
{
    public Guid ProjectId { get; set; }
    public bool OnSuccess { get; set; } = true;
    public bool OnFailure { get; set; } = true;
    public string ProtectedSlackUrl { get; set; } = "";
    public string ProtectedDiscordUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTimeOffset EnabledAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid DeploymentId { get; set; }
    public string Event { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Message { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
}
