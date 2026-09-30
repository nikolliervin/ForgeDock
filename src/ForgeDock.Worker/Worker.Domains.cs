using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed partial class Worker
{
    private HttpsEdge? httpsEdge;
    private readonly DomainDnsVerifier domainVerifier = new(new DomainDnsResolver());

    private Task<List<string>> VerifiedDomains(ForgeDockDbContext db, Guid projectId, CancellationToken ct) =>
        db.CustomDomains.Where(d => d.ProjectId == projectId && d.DnsVerifiedAt != null && d.State != CustomDomainState.Removing)
            .Select(d => d.Hostname).ToListAsync(ct);

    private async Task ReconcileDomains(ForgeDockDbContext db, CancellationToken ct)
    {
        var settings = DomainSettings.From(key => configuration[key]);
        if (!settings.Enabled) return;
        var domains = await db.CustomDomains.ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var domain in domains.Where(d => d.State != CustomDomainState.Removing && d.NextCheckAt <= now &&
            (d.DnsVerifiedAt is null || d.VerificationRequested)))
        {
            var result = await domainVerifier.VerifyAsync(domain.Hostname, domain.VerificationToken, settings, ct);
            domain.VerificationRequested = false;
            domain.NextCheckAt = now.AddMinutes(1);
            domain.Error = result.Error;
            domain.DnsVerifiedAt = result.Verified ? now : null;
            if (!result.Verified) domain.State = CustomDomainState.PendingDns;
        }
        await db.SaveChangesAsync(ct);
        if (settings.Validate() is { } setupError)
        {
            foreach (var domain in domains.Where(d => d.DnsVerifiedAt != null && d.State != CustomDomainState.Removing))
            { domain.State = CustomDomainState.Error; domain.Error = setupError; }
            await db.SaveChangesAsync(ct);
            return;
        }
        httpsEdge ??= new HttpsEdge(runner, configuration["ForgeDock:RuntimePath"] ?? ".runtime", settings);
        var root = Path.GetFullPath(configuration["ForgeDock:RuntimePath"] ?? ".runtime");
        var proxy = configuration["ForgeDock:ProxyContainer"] ?? "forgedock-proxy";
        var written = new Dictionary<string, string>();
        try
        {
            var active = await db.Projects.Where(p => p.ActiveDeploymentId != null).ToListAsync(ct);
            foreach (var project in active)
            {
                var deployment = await db.Deployments.FindAsync([project.ActiveDeploymentId!.Value], ct);
                if (deployment?.State != DeploymentState.Running || deployment.ContainerId is null) continue;
                var path = Path.Combine(root, "routes", $"{project.Id:N}.conf");
                if (!File.Exists(path)) continue; // A stopped project's route must stay removed.
                var before = await File.ReadAllTextAsync(path, ct);
                var snapshot = DeploymentSnapshot.Deserialize(deployment.ConfigurationJson);
                var config = ApplicationRoute.Create(project.Id, deployment.ContainerId, snapshot.ContainerPort,
                    domains.Where(d => d.ProjectId == project.Id && d.DnsVerifiedAt != null && d.State != CustomDomainState.Removing).Select(d => d.Hostname));
                if (before == config) continue;
                written.Add(path, before);
                await File.WriteAllTextAsync(path + ".tmp", config, ct); File.Move(path + ".tmp", path, true);
            }
            if (written.Count > 0)
            {
                await runner.RunAsync("docker", ["exec", proxy, "nginx", "-t"], null, _ => Task.CompletedTask, ct);
                await runner.RunAsync("docker", ["exec", proxy, "nginx", "-s", "reload"], null, _ => Task.CompletedTask, ct);
            }
            await httpsEdge.ApplyAsync(domains.Where(d => d.DnsVerifiedAt != null && d.State != CustomDomainState.Removing).Select(d => d.Hostname), ct);
            foreach (var domain in domains.Where(d => d.DnsVerifiedAt != null && d.State != CustomDomainState.Removing))
            {
                var project = active.FirstOrDefault(p => p.Id == domain.ProjectId);
                var running = project is not null && File.Exists(Path.Combine(root, "routes", $"{project.Id:N}.conf"));
                var certificate = await httpsEdge.ProbeCertificateAsync(domain.Hostname, ct);
                domain.CertificateExpiresAt = certificate.ExpiresAt;
                domain.CertificateTrusted = certificate.Trusted;
                domain.State = !running ? CustomDomainState.AwaitingDeployment : domain.CertificateExpiresAt > now ? (certificate.Trusted || settings.IsStaging ? CustomDomainState.Active : CustomDomainState.Error) : CustomDomainState.Provisioning;
                domain.Error = domain.State == CustomDomainState.Error ? "The certificate is not trusted. Check the configured certificate authority and HTTPS edge logs." : domain.CertificateExpiresAt is { } expiry && expiry <= now ? "The TLS certificate has expired. Check HTTPS edge logs and domain DNS." : null;
            }
            db.CustomDomains.RemoveRange(domains.Where(d => d.State == CustomDomainState.Removing));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            foreach (var (path, before) in written) await File.WriteAllTextAsync(path, before, CancellationToken.None);
            if (written.Count > 0)
                try { await runner.RunAsync("docker", ["exec", proxy, "nginx", "-s", "reload"], null, _ => Task.CompletedTask, ct); }
                catch (Exception recovery) when (recovery is not OperationCanceledException) { logger.LogWarning(recovery, "Could not restore domain aliases"); }
            foreach (var domain in domains.Where(d => d.DnsVerifiedAt != null || d.State == CustomDomainState.Removing))
            {
                if (domain.State != CustomDomainState.Removing) domain.State = CustomDomainState.Error;
                domain.Error = "The HTTPS edge could not apply this configuration. Run make edge and inspect the edge container logs.";
            }
            await db.SaveChangesAsync(ct);
            logger.LogWarning(error, "Custom domain reconciliation failed");
        }
    }
}
