using ForgeDock.Infrastructure;
namespace ForgeDock.Tests;
public class NotificationPolicyTests
{
    [Theory]
    [InlineData("Slack", "https://hooks.slack.com/services/T1/B2/secret", true)]
    [InlineData("Discord", "https://discord.com/api/webhooks/123/secret_abc", true)]
    [InlineData("Slack", "https://hooks.slack.com.evil.example/services/T/B/x", false)]
    [InlineData("Slack", "https://hooks.slack.com@localhost/services/T/B/x", false)]
    [InlineData("Discord", "http://discord.com/api/webhooks/123/secret", false)]
    [InlineData("Discord", "https://discord.com:8443/api/webhooks/123/secret", false)]
    [InlineData("Slack", "https://hooks.slack.com/services/T/B/x?redirect=1", false)]
    public void RestrictsWebhookDestinations(string channel, string url, bool valid) => Assert.Equal(valid, NotificationPolicy.ValidWebhook(channel, url));
    [Fact]
    public void LogLinksIdentifyExactDeployment()
    {
        var project = Guid.NewGuid(); var deployment = Guid.NewGuid();
        Assert.Equal($"https://dashboard.example/projects/{project}/deployments?deployment={deployment}", NotificationPolicy.LogLink("https://dashboard.example/", project, deployment));
    }
    [Theory]
    [InlineData("hello@example.com", true)]
    [InlineData("Name <hello@example.com>", false)]
    [InlineData("hello@example.com\r\nBcc:other@example.com", false)]
    public void ValidatesSingleEmail(string value, bool valid) => Assert.Equal(valid, NotificationPolicy.ValidEmail(value));
}
