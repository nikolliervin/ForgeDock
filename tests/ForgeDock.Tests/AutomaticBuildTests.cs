using ForgeDock.Application;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public sealed class AutomaticBuildTests : IDisposable
{
    private readonly string source = Path.Combine(Path.GetTempPath(), "forgedock-auto-" + Guid.NewGuid().ToString("N"));
    public AutomaticBuildTests() => Directory.CreateDirectory(source);
    public void Dispose() => Directory.Delete(source, true);

    [Fact]
    public void AutoFallsBackOnlyWhenDockerfileIsAbsent()
    {
        Assert.False(SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "Dockerfile"));
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Dockerfile, source, "Dockerfile"));
        File.WriteAllText(Path.Combine(source, "Dockerfile"), "FROM scratch");
        Assert.True(SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "Dockerfile"));
    }

    [Fact]
    public void AutoRejectsTraversalDirectoriesAndDanglingSymlinks()
    {
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "../Dockerfile"));
        Directory.CreateDirectory(Path.Combine(source, "directory"));
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "directory"));
        File.CreateSymbolicLink(Path.Combine(source, "Dockerfile"), "/missing-forgedock-target");
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "Dockerfile"));
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), source);
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.UsesDockerfile(DeploymentMode.Auto, source, "linked/absent"));
    }

    [Fact]
    public void CommandsAreValidatedAndSnapshotsPreserveOriginalSettings()
    {
        Assert.Empty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "Dockerfile", 3000, "/", DeploymentMode.Auto));
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "../Dockerfile", 3000, "/", DeploymentMode.Auto));
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "Dockerfile", 3000, "/", DeploymentMode.Auto, buildCommand: "build\nnext"));
        var project = new Project { DeploymentMode = DeploymentMode.Auto, BuildCommand = "npm run build", StartCommand = "node dist/server.js" };
        var json = DeploymentSnapshot.Create(project, []).Serialize();
        project.StartCommand = "different";
        var snapshot = DeploymentSnapshot.Deserialize(json);
        Assert.Equal(DeploymentMode.Auto, snapshot.DeploymentMode);
        Assert.Equal("npm run build", snapshot.BuildCommand);
        Assert.Equal("node dist/server.js", snapshot.StartCommand);
        var old = DeploymentSnapshot.Deserialize("{\"RepositoryUrl\":\"https://github.com/example/app\",\"Branch\":\"main\",\"Dockerfile\":\"Dockerfile\",\"ContainerPort\":8080,\"HealthPath\":\"/\",\"ProtectedEnvironment\":{}}");
        Assert.Equal(DeploymentMode.Dockerfile, old.DeploymentMode);
        Assert.Equal("", old.StartCommand);
        Assert.Equal(".", old.RootDirectory);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp")]
    [InlineData("backend/../frontend")]
    [InlineData("backend//nested")]
    [InlineData("backend\\nested")]
    [InlineData("")]
    public void RootDirectoryRejectsPathsOutsideTheRepository(string path)
    {
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "Dockerfile", 3000, "/", rootDirectory: path));
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.ResolveRootDirectory(source, path));
    }

    [Fact]
    public void RootDirectoryRejectsMissingDirectoriesAndSymlinkAncestors()
    {
        Assert.Equal(source, SingleApplicationBuilder.ResolveRootDirectory(source, "."));
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.ResolveRootDirectory(source, "missing"));
        File.WriteAllText(Path.Combine(source, "file"), "contents");
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.ResolveRootDirectory(source, "file"));
        var backend = Directory.CreateDirectory(Path.Combine(source, "backend")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), backend);
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.ResolveRootDirectory(source, "linked"));
        Assert.Throws<InvalidOperationException>(() => SingleApplicationBuilder.ResolveRootDirectory(source, "linked/missing"));
        Assert.Equal(backend, SingleApplicationBuilder.ResolveRootDirectory(source, "backend"));
        Assert.NotEmpty(ProjectConfiguration.Validate("Demo", "https://github.com/example/app", "main", "Dockerfile", 3000, "/", DeploymentMode.Compose, composeService: "web", rootDirectory: "backend"));
    }

    [Fact]
    public async Task RailpackBuildsSelectedDirectoryWithLiteralVariablesAndRedactedLogs()
    {
        if (!OperatingSystem.IsLinux()) return;
        var backend = Directory.CreateDirectory(Path.Combine(source, "backend")).FullName;
        var executable = Path.Combine(source, "fake-build");
        var captured = Path.Combine(source, "arguments");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\0' \"$@\" > '" + captured + "'\npwd > '" + captured + ".cwd'\nprintf '%s\\0' \"$BUILDKIT_HOST\" \"${APP_VALUE-unset}\" > '" + captured + ".env'\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var protector = new SecretProtector(Convert.ToBase64String(new byte[32]));
        var variables = new Dictionary<string, string> { ["APP_VALUE"] = "secret-first-line\nsecret-second-line", ["EMPTY"] = "", ["RAILPACK_NODE_VERSION"] = "22", ["LITERAL"] = "a=b; $(echo should-not-run)", ["BUILDKIT_HOST"] = "project-host" };
        var project = new Project { DeploymentMode = DeploymentMode.Auto, RootDirectory = "backend" };
        var snapshot = DeploymentSnapshot.Deserialize(DeploymentSnapshot.Create(project, variables.Select(e => new ProjectEnvironment { Name = e.Key, ProtectedValue = protector.Protect(e.Value) })).Serialize());
        project.RootDirectory = "frontend";
        Assert.Equal("backend", snapshot.RootDirectory);
        var lines = new List<string>();
        await new SingleApplicationBuilder(new ProcessRunner()).BuildAsync(snapshot, source, "test/image", project.Id, executable,
            "docker-container://platform", line => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None,
            snapshot.ProtectedEnvironment.ToDictionary(e => e.Key, e => protector.Unprotect(e.Value)));
        var args = (await File.ReadAllTextAsync(captured)).Split('\0')[..^1];
        var processEnvironment = (await File.ReadAllTextAsync(captured + ".env")).Split('\0');
        Assert.Equal(backend, args.Last());
        Assert.Equal(backend, (await File.ReadAllTextAsync(captured + ".cwd")).Trim());
        Assert.Equal("docker-container://platform", processEnvironment[0]);
        Assert.Equal("unset", processEnvironment[1]);
        foreach (var (name, value) in variables)
        {
            var position = Array.IndexOf(args, $"{name}={value}");
            Assert.True(position > 0);
            Assert.Equal("--env", args[position - 1]);
        }
        Assert.Contains("Build root directory: backend", lines);
        Assert.Contains("APP_VALUE=[REDACTED]", lines);
        Assert.DoesNotContain(lines, line => line.Contains("secret-first-line") || line.Contains("secret-second-line"));
    }

    [Fact]
    public async Task MissingRailpackHasActionableError()
    {
        var snapshot = DeploymentSnapshot.Create(new Project { DeploymentMode = DeploymentMode.Auto }, []);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new SingleApplicationBuilder(new ProcessRunner())
            .BuildAsync(snapshot, source, "test/image", Guid.NewGuid(), Path.Combine(source, "absent"), "unused", _ => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("make railpack", error.Message);
    }

    [Fact]
    public async Task RailpackReceivesLiteralOverridesAndIsolatedEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;
        var executable = Path.Combine(source, "fake-railpack");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' \"$@\"\nprintf 'host=%s token=%s connection=%s\\n' \"$BUILDKIT_HOST\" \"${ForgeDock__ApiToken-unset}\" \"${ConnectionStrings__ForgeDock-unset}\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var snapshot = DeploymentSnapshot.Create(new Project { DeploymentMode = DeploymentMode.Auto,
            BuildCommand = "npm run build; echo literal", StartCommand = "node server.js" }, []);
        var lines = new List<string>();
        await new SingleApplicationBuilder(new ProcessRunner()).BuildAsync(snapshot, source, "test/image:tag", Guid.NewGuid(),
            executable, "docker-container://test-buildkit", line => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Contains("npm run build; echo literal", lines);
        Assert.Contains("--start-cmd", lines);
        Assert.Contains("test/image:tag", lines);
        Assert.Contains("host=docker-container://test-buildkit token=unset connection=unset", lines);
    }
}
