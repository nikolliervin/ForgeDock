using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record ConfigurationIssue(string Severity, string Message);

public sealed record ConfigurationService(string Name, int[] Ports);

public sealed record ConfigurationCheck(
    string[] ComposeFiles,
    string? SelectedComposeFile,
    ConfigurationService[] Services,
    ConfigurationIssue[] Issues,
    string? SuggestedMode = null,
    string? SuggestedBranch = null
);

// Inspects metadata only: does not build images, run hooks, pull images, or start containers.
public static class RepositoryConfigurationInspector
{
    public static string[] FindComposeFiles(string root) =>
        Files(root, root, 0)
            .Take(10000)
            .Where(p =>
                Path.GetFileName(p)
                    is "compose.yaml"
                        or "compose.yml"
                        or "docker-compose.yaml"
                        or "docker-compose.yml"
            )
            .Select(p => Path.GetRelativePath(root, p))
            .Order()
            .ToArray();

    private static IEnumerable<string> Files(string root, string directory, int depth)
    {
        if (depth > 8)
            yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Take(5000))
        {
            if (Path.GetFileName(path) is ".git" or "node_modules" or "bin" or "obj")
                continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            if (Directory.Exists(path))
                foreach (var file in Files(root, path, depth + 1))
                    yield return file;
            else
                yield return path;
        }
    }

    public static ConfigurationCheck InspectCompose(
        string root,
        string json,
        string[] files,
        string selectedFile,
        string selectedService,
        int port
    )
    {
        var issues = new List<ConfigurationIssue>();
        var services = (JsonNode.Parse(json)?["services"] as JsonObject ?? new());
        var choices = services
            .Select(pair => new ConfigurationService(
                pair.Key,
                Ports(pair.Value).Distinct().Order().ToArray()
            ))
            .OrderBy(s => s.Name)
            .ToArray();
        if (string.IsNullOrEmpty(selectedService))
            issues.Add(new("warning", "Choose the service exposed through your app URL."));
        else if (!services.ContainsKey(selectedService))
            issues.Add(
                new(
                    "error",
                    $"Service '{selectedService}' does not exist. Choose one of the discovered services."
                )
            );
        else
        {
            var ports = choices.Single(s => s.Name == selectedService).Ports;
            if (ports.Length == 0)
                issues.Add(
                    new(
                        "warning",
                        "No internal port is declared for this service. Confirm the port the app listens on."
                    )
                );
            else if (!ports.Contains(port))
                issues.Add(
                    new(
                        "warning",
                        $"Port {port} is not a declared internal port for '{selectedService}'. Use {string.Join(" or ", ports)}; host ports are not used by ForgeDock."
                    )
                );
            if (services[selectedService]?["profiles"] is JsonArray { Count: > 0 })
                issues.Add(
                    new(
                        "error",
                        "The selected service requires a Compose profile. Select an always-enabled service or remove its profile."
                    )
                );
        }
        foreach (var (name, service) in services)
        {
            if (service?["profiles"] is JsonArray { Count: > 0 })
                continue;
            if (service?["build"] is { } build)
            {
                var context =
                    build is JsonValue ? build.ToString() : build["context"]?.ToString() ?? ".";
                var dockerfile =
                    build is JsonObject
                        ? build["dockerfile"]?.ToString() ?? "Dockerfile"
                        : "Dockerfile";
                if (build is JsonObject obj && obj.ContainsKey("dockerfile_inline"))
                    issues.Add(
                        new(
                            "warning",
                            $"{name}: inline Dockerfile inputs require build-time validation."
                        )
                    );
                else
                    CheckDockerfile(root, context, dockerfile, issues, name);
            }
        }
        return new(files, selectedFile, choices, issues.ToArray());
    }

    private static IEnumerable<int> Ports(JsonNode? service)
    {
        foreach (var node in (service?["ports"] as JsonArray ?? []))
        {
            if (node is JsonObject obj)
            {
                if (obj["protocol"]?.ToString() is { } protocol && protocol != "tcp")
                    continue;
                if (
                    int.TryParse(obj["target"]?.ToString(), out var value)
                    && value is > 0 and <= 65535
                )
                    yield return value;
            }
            else
            {
                var text = node?.ToString() ?? "";
                if (text.EndsWith("/udp"))
                    continue;
                text = text.Split('/')[0].Split(':')[^1];
                if (int.TryParse(text, out var value) && value is > 0 and <= 65535)
                    yield return value;
            }
        }
        foreach (var node in (service?["expose"] as JsonArray ?? []))
        {
            var text = node?.ToString() ?? "";
            if (
                !text.EndsWith("/udp")
                && int.TryParse(text.Split('/')[0], out var value)
                && value is > 0 and <= 65535
            )
                yield return value;
        }
    }

    public static ConfigurationCheck InspectSingle(
        string root,
        DeploymentMode mode,
        string directory,
        string dockerfile,
        int port
    )
    {
        var issues = new List<ConfigurationIssue>();
        var context = ComposeDefinition.RepositoryPath(root, directory);
        var file = ComposeDefinition.RepositoryPath(root, Path.Combine(directory, dockerfile));
        if (mode == DeploymentMode.Dockerfile || File.Exists(file))
        {
            CheckDockerfile(root, directory, dockerfile, issues, "App");
            if (File.Exists(file))
            {
                var ports = Regex
                    .Matches(File.ReadAllText(file), @"(?im)^\s*EXPOSE\s+(\d+)(?:/tcp)?\s*$")
                    .Select(m => int.Parse(m.Groups[1].Value))
                    .ToArray();
                if (ports.Length > 0 && !ports.Contains(port))
                    issues.Add(
                        new(
                            "warning",
                            $"Dockerfile declares internal port {string.Join(" or ", ports)}; configured port is {port}. Confirm the app's listening port."
                        )
                    );
            }
        }
        else if (!Directory.Exists(context))
            issues.Add(new("error", $"Root directory '{directory}' does not exist."));
        else if (
            !Directory
                .EnumerateFiles(context)
                .Any(p =>
                    Path.GetExtension(p) is ".csproj" or ".fsproj"
                    || Path.GetFileName(p)
                        is "package.json"
                            or "requirements.txt"
                            or "pyproject.toml"
                            or "go.mod"
                            or "Cargo.toml"
                            or "pom.xml"
                            or "build.gradle"
                            or "Gemfile"
                            or "composer.json"
                            or "index.html"
                            or "start.sh"
                )
        )
            issues.Add(
                new(
                    "warning",
                    "No common application entry file was found in the build root. A solution file alone does not let Railpack detect a .NET service; select the service directory or use Compose."
                )
            );
        issues.Add(
            new(
                "info",
                "Static checks cannot verify runtime dependencies or the HTTP health endpoint."
            )
        );
        return new(FindComposeFiles(root), null, [], issues.ToArray());
    }

    private static void CheckDockerfile(
        string root,
        string context,
        string dockerfile,
        List<ConfigurationIssue> issues,
        string service
    )
    {
        try
        {
            var directory = ComposeDefinition.RepositoryPath(root, context);
            var file = ComposeDefinition.RepositoryPath(root, Path.Combine(context, dockerfile));
            if (!Directory.Exists(directory) || !File.Exists(file))
            {
                issues.Add(
                    new(
                        "error",
                        $"{service}: build context or Dockerfile '{dockerfile}' is missing."
                    )
                );
                return;
            }
            var text = File.ReadAllText(file);
            foreach (
                Match instruction in Regex.Matches(
                    text.Replace("\\\n", " "),
                    @"(?im)^\s*(COPY|ADD)\s+(.+)$"
                )
            )
            {
                var args = instruction.Groups[2].Value.Trim();
                if (Regex.IsMatch(args, @"--from(?:=|\s)"))
                    continue;
                args = Regex.Replace(args, @"^((--[\w-]+(?:=\S+)?)[ \t]+)+", "");
                string[] parts;
                if (args.StartsWith('['))
                    parts = JsonNode.Parse(args)!.AsArray().Select(n => n!.ToString()).ToArray();
                else
                    parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                foreach (var input in parts.SkipLast(1))
                {
                    if (input.Contains('$') || input.Contains("://"))
                    {
                        issues.Add(
                            new(
                                "warning",
                                $"{service}: dynamic or remote build inputs require build-time validation."
                            )
                        );
                        continue;
                    }
                    var relative =
                        Path.GetRelativePath(root, directory) + "/" + input.TrimStart('/');
                    var full = ComposeDefinition.RepositoryPath(root, relative);
                    if (input.IndexOfAny(['*', '?', '[']) >= 0)
                        continue;
                    if (!File.Exists(full) && !Directory.Exists(full))
                        issues.Add(
                            new(
                                "error",
                                $"{service}: Dockerfile {instruction.Groups[1].Value} input '{input}' is missing from its build context. Generate it before building or update the Dockerfile."
                            )
                        );
                }
            }
        }
        catch (Exception error)
            when (error
                    is InvalidOperationException
                        or System.Text.Json.JsonException
                        or IOException
            )
        {
            issues.Add(
                new(
                    "warning",
                    $"{service}: some Dockerfile inputs could not be inspected. Check its context, paths, and syntax."
                )
            );
        }
    }
}
