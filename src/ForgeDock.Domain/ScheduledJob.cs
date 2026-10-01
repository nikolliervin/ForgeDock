namespace ForgeDock.Domain;

public sealed class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public int IntervalMinutes { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class JobRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? ScheduledJobId { get; set; }
    public Guid SourceDeploymentId { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public string State { get; set; } = "Queued";
    public string? ContainerId { get; set; }
    public string Output { get; set; } = "";
    public int? ExitCode { get; set; }
    public bool Truncated { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
