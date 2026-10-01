using System.Net;
using System.Net.Mail;
using System.Text.Json;
using ForgeDock.Application;

namespace ForgeDock.Infrastructure;

public sealed record DomainSettings(
    bool Enabled,
    string Target,
    string[] Addresses,
    string Email,
    string AcmeDirectory = "https://acme-v02.api.letsencrypt.org/directory",
    string EdgeContainer = "forgedock-edge",
    int HttpsPort = 8443,
    string BindAddress = "127.0.0.1"
)
{
    /// <summary>
    /// Reads optional edge settings with loopback defaults; enabling domains still requires explicit
    /// validation.
    /// </summary>
    public static DomainSettings From(Func<string, string?> get) =>
        new(
            bool.TryParse(get("ForgeDock:Domains:Enabled"), out var enabled) && enabled,
            (get("ForgeDock:Domains:Target") ?? "").Trim().TrimEnd('.').ToLowerInvariant(),
            (get("ForgeDock:Domains:Addresses") ?? "").Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            ),
            (get("ForgeDock:Domains:Email") ?? "").Trim(),
            get("ForgeDock:Domains:AcmeDirectory")
                ?? "https://acme-v02.api.letsencrypt.org/directory",
            get("ForgeDock:Domains:EdgeContainer") ?? "forgedock-edge",
            int.TryParse(get("ForgeDock:Domains:HttpsPort"), out var port) ? port : 8443,
            get("ForgeDock:Domains:BindAddress") ?? "127.0.0.1"
        );

    /// <summary>
    /// Returns an actionable configuration error before DNS checks or HTTPS configuration are attempted.
    /// </summary>
    public string? Validate()
    {
        if (!Enabled)
            return "Custom domains are not enabled on this server.";
        if (!DomainName.TryNormalize(Target, out var target) || target != Target)
            return "Set ForgeDock__Domains__Target to the server's public DNS hostname.";
        if (Addresses.Length == 0 || Addresses.Any(value => !IPAddress.TryParse(value, out _)))
            return "Set ForgeDock__Domains__Addresses to the server's comma-separated public IP addresses.";
        if (!MailAddress.TryCreate(Email, out var email) || email.Address != Email)
            return "Set ForgeDock__Domains__Email to a valid certificate registration email.";
        if (
            !Uri.TryCreate(AcmeDirectory, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
            return "The ACME directory must be an HTTPS URL.";
        if (HttpsPort is < 1 or > 65535 || BindAddress is not ("127.0.0.1" or "0.0.0.0" or "::"))
            return "Configure a valid HTTPS edge port and bind address.";
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                EdgeContainer,
                @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"
            )
        )
            return "The edge container name is invalid.";
        return null;
    }

    public bool IsStaging => AcmeDirectory != "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Quotes operator-controlled strings for edge configuration using JSON-compatible escaping.
    /// </summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value);
}
