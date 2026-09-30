using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public sealed class DomainRoutingTests
{
    private static readonly DomainSettings Settings = new(true, "deploy.example.com", ["203.0.113.10"], "ops@example.com");
    [Fact]
    public void EdgeOnlyIncludesExplicitVerifiedNamesAndKeepsItsAdminPrivate()
    {
        var config = HttpsEdge.BuildConfiguration(Settings, ["app.example.com", "api.example.com", "app.example.com"]);
        Assert.Contains("admin localhost:2019", config);
        Assert.Contains("https://app.example.com {\n  reverse_proxy forgedock-proxy:80\n}", config);
        Assert.Equal(1, config.Split("https://app.example.com").Length - 1);
        Assert.DoesNotContain("on_demand", config);
        Assert.Throws<InvalidOperationException>(() => HttpsEdge.BuildConfiguration(Settings, ["app.example.com { respond evil }"]));
    }
    [Fact]
    public void RoutesPreserveLocalAccessAndForwardTheOriginalHttpsScheme()
    {
        var project = Guid.NewGuid();
        var config = ApplicationRoute.Create(project, "forgedock-abc", 3000, ["app.example.com"]);
        Assert.Contains($"server_name {project:N}.localhost app.example.com;", config);
        Assert.Contains("proxy_set_header Host $http_host", config);
        Assert.Contains("proxy_set_header X-Forwarded-Proto $forgedock_scheme", config);
        Assert.Contains("resolver 127.0.0.11", config);
        Assert.Contains("return 404", ApplicationRoute.BootstrapConfiguration);
        Assert.Throws<InvalidOperationException>(() => ApplicationRoute.Create(project, "forgedock-abc;evil", 3000, []));
        Assert.Throws<InvalidOperationException>(() => ApplicationRoute.Create(project, "forgedock-abc", 3000, ["*.example.com"]));
    }
}
