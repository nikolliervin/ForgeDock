using ForgeDock.Application;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed class SingleApplicationBuilder(ProcessRunner runner)
{
    public static bool UsesDockerfile(DeploymentMode mode, string source, string relativePath)
    {
        if (mode is not (DeploymentMode.Auto or DeploymentMode.Dockerfile))
            throw new InvalidOperationException("Single application builds require Auto or Dockerfile mode.");
        if (!ProjectConfiguration.IsRepositoryPath(relativePath))
            throw new InvalidOperationException("Dockerfile must be a relative path within the repository.");
        var root = Path.GetFullPath(source);
        var dockerfile = new FileInfo(Path.Combine(root, relativePath));
        FileSystemInfo? current = dockerfile;
        while (current is not null && current.FullName != root)
        {
            if (current.LinkTarget is not null)
                throw new InvalidOperationException("Dockerfile path must not contain symbolic links.");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        if (dockerfile.Exists) return true;
        if (Directory.Exists(dockerfile.FullName))
            throw new InvalidOperationException("Configured Dockerfile path is a directory.");
        if (mode == DeploymentMode.Dockerfile)
            throw new InvalidOperationException("Configured Dockerfile is missing from the repository.");
        return false;
    }

    public static string ResolveRootDirectory(string source, string relativePath)
    {
        if (relativePath != "." && !ProjectConfiguration.IsRepositoryPath(relativePath))
            throw new InvalidOperationException("Root directory must be '.' or a relative directory within the repository.");
        var root = Path.GetFullPath(source);
        var directory = new DirectoryInfo(Path.Combine(root, relativePath));
        for (DirectoryInfo? current = directory; current is not null && current.FullName != root; current = current.Parent)
            if (current.LinkTarget is not null)
                throw new InvalidOperationException("Root directory must not contain symbolic links.");
        if (!directory.Exists)
            throw new InvalidOperationException($"Configured root directory '{relativePath}' is missing from the repository.");
        return directory.FullName;
    }

    public async Task BuildAsync(DeploymentSnapshot snapshot, string source, string imageTag, Guid projectId,
        string railpackPath, string buildkitHost, Func<string, Task> log, CancellationToken ct,
        IReadOnlyDictionary<string, string>? buildEnvironment = null)
    {
        source = ResolveRootDirectory(source, snapshot.RootDirectory);
        await log($"Build root directory: {snapshot.RootDirectory}");
        if (UsesDockerfile(snapshot.DeploymentMode, source, snapshot.Dockerfile))
        {
            await log("Builder: Dockerfile");
            await runner.RunAsync("docker", ["build", "--label", "io.forgedock.managed=true", "--label",
                $"io.forgedock.project={projectId}", "-t", imageTag, "-f", Path.Combine(source, snapshot.Dockerfile), source],
                null, log, ct, inheritEnvironment: false);
            return;
        }
        if (!File.Exists(railpackPath))
            throw new InvalidOperationException("Railpack is not installed. Run make railpack before using Auto builds.");
        await log("Builder: Railpack — detecting the application runtime");
        var arguments = new List<string> { "build", "--name", imageTag, "--progress", "plain", "--error-missing-start" };
        if (!string.IsNullOrWhiteSpace(snapshot.BuildCommand)) arguments.AddRange(["--build-cmd", snapshot.BuildCommand]);
        if (!string.IsNullOrWhiteSpace(snapshot.StartCommand)) arguments.AddRange(["--start-cmd", snapshot.StartCommand]);
        if (buildEnvironment is not null)
            foreach (var (name, value) in buildEnvironment)
                arguments.AddRange(["--env", $"{name}={value}"]);
        // Output is drained line by line, so redact each line of multiline values too.
        var secrets = (buildEnvironment?.Values ?? [])
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Distinct().OrderByDescending(value => value.Length).ToArray();
        Task BuildLog(string line)
        {
            foreach (var value in secrets) line = line.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            return log(line);
        }
        arguments.Add(source);
        await runner.RunAsync(railpackPath, arguments, source, BuildLog, ct, inheritEnvironment: false,
            environment: new Dictionary<string, string> { ["BUILDKIT_HOST"] = buildkitHost });
    }
}
