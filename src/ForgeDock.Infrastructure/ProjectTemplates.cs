using System.IO.Compression;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record ProjectTemplate(
    string Id,
    string Name,
    string Description,
    DeploymentMode DeploymentMode,
    int ContainerPort,
    string HealthPath = "/health",
    string Dockerfile = "Dockerfile",
    string RootDirectory = ".",
    string BuildCommand = "",
    string StartCommand = "",
    string ComposeFile = "compose.yaml",
    string ComposeService = "",
    DatabaseKind? SuggestedDatabase = null
);

public static class ProjectTemplates
{
    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        new(
            "node",
            "Node.js API",
            "Package scripts and PORT-based HTTP server. Add your database client when needed.",
            DeploymentMode.Auto,
            8080,
            SuggestedDatabase: DatabaseKind.PostgreSql
        ),
        new(
            "nextjs",
            "Next.js",
            "App Router with a production build and server listening on all interfaces.",
            DeploymentMode.Auto,
            3000,
            BuildCommand: "npm run build",
            StartCommand: "npm run start -- --port $PORT"
        ),
        new(
            "vite",
            "React / Vite",
            "Build static assets and serve with nginx. The starter includes a Dockerfile.",
            DeploymentMode.Auto,
            8080,
            BuildCommand: "npm run build"
        ),
        new(
            "fastapi",
            "Python / FastAPI",
            "Uvicorn serves main:app. Change the module for your repository.",
            DeploymentMode.Auto,
            8000,
            StartCommand: "uvicorn main:app --host 0.0.0.0 --port $PORT",
            SuggestedDatabase: DatabaseKind.PostgreSql
        ),
        new(
            "go",
            "Go HTTP service",
            "Standard library HTTP server; build and start are detected automatically.",
            DeploymentMode.Auto,
            8080
        ),
        new(
            "aspnet",
            "ASP.NET Core",
            ".NET 10 minimal API listening on PORT with a health endpoint.",
            DeploymentMode.Auto,
            8080,
            SuggestedDatabase: DatabaseKind.PostgreSql
        ),
        new(
            "compose",
            "Node.js + Redis stack",
            "Compose with a public web service and private persistent Redis dependency.",
            DeploymentMode.Compose,
            8080,
            ComposeService: "web"
        ),
    ];

    public static ProjectTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Builds a starter ZIP from embedded resources for a known template; no repository-provided paths are
    /// used.
    /// </summary>
    public static byte[] Archive(string id)
    {
        if (Find(id) is null)
            throw new ArgumentException("Unknown project template.");
        var assembly = typeof(ProjectTemplates).Assembly;
        var prefix = "ForgeDock.Template." + id + "/";
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (
                var resource in assembly
                    .GetManifestResourceNames()
                    .Where(r => r.StartsWith(prefix, StringComparison.Ordinal))
                    .Order()
            )
            {
                var relative = resource[prefix.Length..];
                using var source = assembly.GetManifestResourceStream(resource)!;
                using var entry = archive.CreateEntry(relative).Open();
                source.CopyTo(entry);
            }
        return output.ToArray();
    }
}
