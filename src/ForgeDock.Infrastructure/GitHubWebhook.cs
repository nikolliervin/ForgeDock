using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeDock.Application;

namespace ForgeDock.Infrastructure;

public static class GitHubWebhook
{
    /// <summary>
    /// Authenticates the original request bytes with HMAC-SHA256 and a constant-time comparison; parsing or
    /// reserializing first would change the signed payload.
    /// </summary>
    public static bool VerifySignature(ReadOnlySpan<byte> body, string secret, string? signature)
    {
        if (
            string.IsNullOrEmpty(secret)
            || signature is null
            || signature.Length != 71
            || !signature.StartsWith("sha256=", StringComparison.Ordinal)
        )
            return false;
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature[7..]);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body),
            supplied
        );
    }

    /// <summary>
    /// Canonicalizes supported github.com HTTPS repository URLs for case-insensitive event matching without
    /// credentials or alternate origins.
    /// </summary>
    public static string? RepositoryIdentity(string url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || uri.Host != "github.com"
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
        )
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => !Regex.IsMatch(p, "^[A-Za-z0-9_.-]+$")))
            return null;
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        return name.Length == 0 ? null : (parts[0] + "/" + name).ToLowerInvariant();
    }

    /// <summary>
    /// Accepts only the configured repository and branch, ignores deletion events, and returns a normalized
    /// full commit SHA for immutable queueing.
    /// </summary>
    public static (string Status, string? CommitSha) EvaluatePush(
        ReadOnlyMemory<byte> body,
        string repositoryUrl,
        string branch
    )
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var repo = root.GetProperty("repository").GetProperty("html_url").GetString();
        var configured = RepositoryIdentity(repositoryUrl);
        if (configured is null || repo is null || RepositoryIdentity(repo) != configured)
            return ("IgnoredRepository", null);
        var reference = root.GetProperty("ref").GetString();
        if (reference != "refs/heads/" + branch)
            return ("IgnoredBranch", null);
        if (root.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean())
            return ("IgnoredDeletedRef", null);
        var sha = root.GetProperty("after").GetString();
        if (sha is null || !ProjectConfiguration.IsCommitSha(sha))
            throw new FormatException("Push must contain a full commit SHA.");
        if (sha.All(c => c == '0'))
            return ("IgnoredDeletedRef", null);
        return ("Queued", sha.ToLowerInvariant());
    }
}

public sealed class ProjectWebhook
{
    public Guid ProjectId { get; set; }
    public bool Enabled { get; set; }
    public string ProtectedSecret { get; set; } = "";
}

public sealed class WebhookDelivery
{
    public Guid ProjectId { get; set; }
    public Guid DeliveryId { get; set; }
    public string Event { get; set; } = "";
    public string Status { get; set; } = "";
    public Guid? DeploymentId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
