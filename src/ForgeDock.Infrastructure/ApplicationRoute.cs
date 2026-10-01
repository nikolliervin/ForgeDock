using System.Text.RegularExpressions;
using ForgeDock.Application;

namespace ForgeDock.Infrastructure;

public static class ApplicationRoute
{
    public const string BootstrapConfiguration =
        "map $http_x_forwarded_proto $forgedock_scheme { default $scheme; https https; }\nserver { listen 80 default_server; server_name _; return 404; }\n";

    /// <summary>
    /// Builds nginx configuration only from validated upstream names, ports, and normalized hostnames.
    /// Docker DNS resolves the variable upstream so missing containers do not prevent nginx startup.
    /// </summary>
    public static string Create(
        Guid projectId,
        string container,
        int port,
        IEnumerable<string> domains
    )
    {
        if (!Regex.IsMatch(container, @"^forgedock-[a-zA-Z0-9_.-]+$") || port is < 1 or > 65535)
            throw new InvalidOperationException("Invalid application upstream.");
        var hosts = domains.Distinct().Order(StringComparer.Ordinal).ToArray();
        if (
            hosts.Any(host =>
                !DomainName.TryNormalize(host, out var normalized) || host != normalized
            )
        )
            throw new InvalidOperationException("Invalid custom domain in application route.");
        var names = string.Join(" ", new[] { $"{projectId:N}.localhost" }.Concat(hosts));
        return $"server {{ listen 80; server_name {names}; location / {{ resolver 127.0.0.11 valid=10s; set $forgedock_upstream http://{container}:{port}; proxy_pass $forgedock_upstream; proxy_set_header Host $http_host; proxy_set_header X-Forwarded-Host $http_host; proxy_set_header X-Forwarded-Proto $forgedock_scheme; proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; }} }}";
    }
}
