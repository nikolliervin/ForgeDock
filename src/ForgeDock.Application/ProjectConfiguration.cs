using System.Text.RegularExpressions;

namespace ForgeDock.Application;

public static partial class ProjectConfiguration
{
    public static IReadOnlyList<string> Validate(string name, string repositoryUrl, string branch,
        string dockerfile, int containerPort, string healthPath)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) errors.Add("Name must contain 1–100 characters.");
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback || uri.Port != 443 ||
            System.Net.IPAddress.TryParse(uri.Host, out _) || !uri.Host.Contains('.') ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            errors.Add("Repository must be a public HTTPS URL without credentials, IP literals, query, or fragment.");
        if (string.IsNullOrEmpty(branch) || branch.Length > 200 || !BranchPattern().IsMatch(branch) ||
            branch.Contains("..") || branch.Contains("//") || branch.EndsWith('/') || branch.EndsWith('.') ||
            branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            errors.Add("Branch must be a valid named Git branch.");
        if (string.IsNullOrWhiteSpace(dockerfile) || Path.IsPathRooted(dockerfile) ||
            dockerfile.Contains('\\') || dockerfile.Split('/').Any(p => p is ".." or "." or "") ||
            dockerfile.Any(char.IsControl))
            errors.Add("Dockerfile must be a relative path within the repository.");
        if (containerPort is < 1 or > 65535) errors.Add("Container port must be between 1 and 65535.");
        if (string.IsNullOrEmpty(healthPath) || !healthPath.StartsWith('/') || healthPath.StartsWith("//") ||
            healthPath.Contains('\\') || healthPath.Any(char.IsControl)) errors.Add("Health path must be a local absolute HTTP path.");
        return errors;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex BranchPattern();
}
