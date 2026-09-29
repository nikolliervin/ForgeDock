using System.Text.RegularExpressions;
using ForgeDock.Domain;

namespace ForgeDock.Application;

public static partial class ProjectConfiguration
{
    public static IReadOnlyList<string> Validate(string name, string repositoryUrl, string branch,
        string dockerfile, int containerPort, string healthPath, DeploymentMode deploymentMode = DeploymentMode.Dockerfile,
        string composeFile = "docker-compose.yml", string composeService = "")
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
        if (!Enum.IsDefined(deploymentMode)) errors.Add("Unsupported deployment mode.");
        if (deploymentMode == DeploymentMode.Dockerfile && !IsRepositoryPath(dockerfile))
            errors.Add("Dockerfile must be a relative path within the repository.");
        if (deploymentMode == DeploymentMode.Compose)
        {
            if (!IsRepositoryPath(composeFile)) errors.Add("Compose file must be a relative path within the repository.");
            if (string.IsNullOrEmpty(composeService) || composeService.Length > 100 || !ServicePattern().IsMatch(composeService))
                errors.Add("Select the Compose service to expose through the application route.");
        }
        if (containerPort is < 1 or > 65535) errors.Add("Container port must be between 1 and 65535.");
        if (string.IsNullOrEmpty(healthPath) || !healthPath.StartsWith('/') || healthPath.StartsWith("//") ||
            healthPath.Contains('\\') || healthPath.Any(char.IsControl)) errors.Add("Health path must be a local absolute HTTP path.");
        return errors;
    }

    public static bool IsRepositoryPath(string value) => !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) && !value.Contains('\\') &&
        !value.Split('/').Any(p => p is ".." or "." or "") && !value.Any(char.IsControl);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ServicePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex BranchPattern();
}
