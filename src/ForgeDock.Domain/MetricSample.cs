namespace ForgeDock.Domain;

public sealed class MetricSample
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DeploymentId { get; set; }
    public string ContainerId { get; set; } = "";
    public string Service { get; set; } = "app";
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public long NetworkReceivedBytes { get; set; }
    public long NetworkSentBytes { get; set; }
    public long BlockReadBytes { get; set; }
    public long BlockWrittenBytes { get; set; }
    public int Pids { get; set; }
}
