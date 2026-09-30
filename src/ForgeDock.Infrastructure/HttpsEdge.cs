using ForgeDock.Application;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ForgeDock.Infrastructure;

public sealed class HttpsEdge(ProcessRunner runner, string runtimePath, DomainSettings settings)
{
    private string? appliedConfiguration;
    public static string BuildConfiguration(DomainSettings settings, IEnumerable<string> domains)
    {
        if (settings.Validate() is { } error) throw new InvalidOperationException(error);
        var hosts = domains.Distinct().Order(StringComparer.Ordinal).ToArray();
        if (hosts.Any(host => !DomainName.TryNormalize(host, out var normalized) || normalized != host))
            throw new InvalidOperationException("Invalid custom domain in HTTPS configuration.");
        var result = new StringBuilder();
        result.AppendLine("{").AppendLine("  admin localhost:2019").AppendLine("  persist_config off")
            .AppendLine($"  email {DomainSettings.Quote(settings.Email)}")
            .AppendLine($"  acme_ca {DomainSettings.Quote(settings.AcmeDirectory)}").AppendLine("}")
            .AppendLine(":80 { respond \"Not found\" 404 }");
        foreach (var host in hosts)
            result.AppendLine($"https://{host} {{ reverse_proxy forgedock-proxy:80 }}");
        return result.ToString();
    }

    public async Task ApplyAsync(IEnumerable<string> domains, CancellationToken ct)
    {
        var config = BuildConfiguration(settings, domains);
        if (config == appliedConfiguration) return;
        var owned = await runner.RunAsync("docker", ["inspect", "--format", "{{index .Config.Labels \"io.forgedock.managed\"}}", settings.EdgeContainer],
            null, _ => Task.CompletedTask, ct, inheritEnvironment: false);
        if (owned != "true") throw new InvalidOperationException("The HTTPS edge container lacks an ownership label.");
        var directory = Path.Combine(Path.GetFullPath(runtimePath), "edge", "config");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Caddyfile");
        var before = File.Exists(file) ? await File.ReadAllTextAsync(file, ct) : null;
        await File.WriteAllTextAsync(file + ".tmp", config, ct);
        File.Move(file + ".tmp", file, overwrite: true);
        try
        {
            await runner.RunAsync("docker", ["exec", settings.EdgeContainer, "caddy", "reload", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"],
                null, _ => Task.CompletedTask, ct, inheritEnvironment: false);
            appliedConfiguration = config;
        }
        catch
        {
            if (before is not null) await File.WriteAllTextAsync(file, before, CancellationToken.None);
            else File.Delete(file);
            throw;
        }
    }

    public async Task<CertificateStatus> ProbeCertificateAsync(string hostname, CancellationToken ct)
    {
        if (!DomainName.TryNormalize(hostname, out var normalized) || normalized != hostname)
            throw new InvalidOperationException("Invalid certificate hostname.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        DateTimeOffset? expires = null;
        var trusted = false;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.BindAddress == "::" ? "::1" : "127.0.0.1", settings.HttpsPort, timeout.Token);
            using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;
                using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                expires = new DateTimeOffset(copy.NotAfter.ToUniversalTime());
                trusted = errors == SslPolicyErrors.None;
                // This local status probe reads the certificate even for staging CAs.
                // It never sends application traffic and still requires a matching hostname.
                return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = hostname,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            return new(expires, trusted);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or SocketException or AuthenticationException or OperationCanceledException)
        { return new(null, false); }
    }
}

public sealed record CertificateStatus(DateTimeOffset? ExpiresAt, bool Trusted);
