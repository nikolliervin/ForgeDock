using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class ComposeDefinitionTests
{
    private const string Source = "/tmp/forgedock-compose-tests";
    private const string Model = """
    {"services":{"web":{"build":{"context":"web"},"container_name":"unrelated-name","ports":[{"target":80,"published":"8080"}],"networks":{"default":null}},
    "db":{"image":"postgres:17-alpine","volumes":[{"type":"volume","source":"data","target":"/var/lib/postgresql/data"}],"networks":{"default":null}}},
    "networks":{"default":{"name":"unrelated-network"}},"volumes":{"data":{"name":"unrelated-volume"}}}
    """;

    [Fact]
    public void ConvertsAnonymousVolumesToStableOwnedVolumes()
    {
        const string json = """{"services":{"web":{"image":"nginx","volumes":[{"type":"volume","target":"/app/data"}]}}}""";
        var project = Guid.NewGuid();
        var first = ComposeDefinition.Normalize(json, Source, project, Guid.NewGuid(), "web", "forgedock");
        var second = ComposeDefinition.Normalize(json, Source, project, Guid.NewGuid(), "web", "forgedock");
        Assert.Equal(first["volumes"]!.ToJsonString(), second["volumes"]!.ToJsonString());
        Assert.NotNull(first["services"]!["web"]!["volumes"]![0]!["source"]);
    }

    [Fact]
    public void RepositorySecretsUseReadOnlyRelabeledMounts()
    {
        const string json = """
        {"services":{"web":{"image":"nginx","secrets":[{"source":"password","target":"db-password"}]}},"secrets":{"password":{"file":"password.txt"}}}
        """;
        var model = ComposeDefinition.Normalize(json, Source, Guid.NewGuid(), Guid.NewGuid(), "web", "forgedock");
        var mount = model["services"]!["web"]!["volumes"]![0]!;
        Assert.Equal("/run/secrets/db-password", mount["target"]!.GetValue<string>());
        Assert.True(mount["read_only"]!.GetValue<bool>());
        Assert.Equal("z", mount["bind"]!["selinux"]!.GetValue<string>());
        Assert.Null(model["secrets"]);
    }

    [Fact]
    public void ScopesResourcesAndRoutesOnlySelectedService()
    {
        var project = Guid.NewGuid(); var deployment = Guid.NewGuid();
        var model = ComposeDefinition.Normalize(Model, Source, project, deployment, "web", "forgedock");
        Assert.Null(model["services"]!["web"]!["ports"]);
        Assert.Equal(ComposeDefinition.ContainerName(project, "web"), model["services"]!["web"]!["container_name"]!.GetValue<string>());
        Assert.Equal(ComposeDefinition.ImageName(project, deployment, "web"), model["services"]!["web"]!["image"]!.GetValue<string>());
        Assert.NotNull(model["services"]!["web"]!["networks"]!["forgedock_ingress"]);
        Assert.Null(model["services"]!["db"]!["networks"]!["forgedock_ingress"]);
        Assert.StartsWith(ComposeDefinition.StackName(project), model["volumes"]!["data"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("privileged", "true")]
    [InlineData("network_mode", "\"host\"")]
    [InlineData("devices", "[\"/dev/sda\"]")]
    [InlineData("cap_add", "[\"SYS_ADMIN\"]")]
    public void RejectsHostAccess(string key, string value)
    {
        var json = "{\"services\":{\"web\":{\"image\":\"nginx\",\""+key+"\":"+value+"}}}";
        Assert.Throws<InvalidOperationException>(() => ComposeDefinition.Normalize(json, Source, Guid.NewGuid(), Guid.NewGuid(), "web", "forgedock"));
    }

    [Fact]
    public void RejectsHostFilesystemMounts() => Assert.Throws<InvalidOperationException>(() =>
        ComposeDefinition.Normalize("""
          {"services":{"web":{"image":"nginx","volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock"}]}}}
          """, Source, Guid.NewGuid(), Guid.NewGuid(), "web", "forgedock"));

    [Fact]
    public void VolumesAreStableAcrossDeployments()
    {
        var project = Guid.NewGuid();
        var first = ComposeDefinition.Normalize(Model, Source, project, Guid.NewGuid(), "web", "forgedock");
        var second = ComposeDefinition.Normalize(Model, Source, project, Guid.NewGuid(), "web", "forgedock");
        Assert.Equal(first["volumes"]!.ToJsonString(), second["volumes"]!.ToJsonString());
        Assert.NotEqual(first["services"]!["web"]!["image"]!.GetValue<string>(), second["services"]!["web"]!["image"]!.GetValue<string>());
    }

    [Fact]
    public void RequiresTheSelectedService() => Assert.Throws<InvalidOperationException>(() =>
        ComposeDefinition.Normalize(Model, Source, Guid.NewGuid(), Guid.NewGuid(), "missing", "forgedock"));
}
