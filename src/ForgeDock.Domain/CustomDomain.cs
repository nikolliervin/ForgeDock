namespace ForgeDock.Domain;

public enum CustomDomainState { PendingDns, AwaitingDeployment, Provisioning, Active, Error, Removing }

public sealed class CustomDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Hostname { get; set; } = "";
    public string VerificationToken { get; set; } = "";
    public CustomDomainState State { get; set; } = CustomDomainState.PendingDns;
    public bool VerificationRequested { get; set; } = true;
    public DateTimeOffset? DnsVerifiedAt { get; set; }
    public bool CertificateTrusted { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
