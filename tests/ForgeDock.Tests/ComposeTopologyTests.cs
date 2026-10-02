using System.Text.Json;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class ComposeTopologyTests
{
    [Fact]
    public void ExposesOnlyDeclaredConnectionsAndNamedStorage()
    {
        var topology = ComposeTopology.Read("""
            {"services": {
              "web": {"depends_on":{"db":{"condition":"service_healthy"},"missing":{},"web":{}},
                "networks":{"default":{},"ingress":{}}, "environment":{"PASSWORD":"secret-value"},
                "command":"private-command", "volumes":[
                  {"type":"bind","source":"/private/host-path","target":"/app"},
                  {"type":"volume","source":"data","target":"/private/target"}]},
              "db": {"depends_on":["web"],"networks":["default"]}},
              "networks":{"ingress":{"name":"private-external-network"}}}
            """, "web");
        Assert.Equal("web", topology.EntryService);
        var web = Assert.Single(topology.Services, s => s.Name == "web");
        Assert.Equal(["db"], web.Dependencies);
        Assert.Equal(["default", "ingress"], web.Networks);
        Assert.Equal(["data"], web.Volumes);
        Assert.Equal(["web"], topology.Services.Single(s => s.Name == "db").Dependencies);
        var json = JsonSerializer.Serialize(topology);
        foreach (var secret in new[] { "secret-value", "private-command", "/private/host-path", "/private/target", "private-external-network" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public void UnpreparedDeploymentHasNoInventedConnections()
    {
        Assert.Empty(ComposeTopology.Read(null, "web").Services);
        Assert.Empty(ComposeTopology.Read("{}", "web").Services);
    }
}
