using System.Text;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public sealed class GitRepositoryTests
{
    [Theory]
    [InlineData("https://github.com/owner/private.git", "token", "1")]
    [InlineData("https://github.com/owner/private.git", null, "0")]
    [InlineData("https://github.com.evil.test/owner/private.git", "token", "0")]
    [InlineData("https://example.com/owner/private.git", "token", "0")]
    [InlineData("http://github.com/owner/private.git", "token", "0")]
    [InlineData("https://github.com:8443/owner/private.git", "token", "0")]
    [InlineData("https://user@github.com/owner/private.git", "token", "0")]
    public void CredentialIsOnlySentToGitHubHttps(string url, string? token, string count)
    {
        var environment = GitRepository.AuthenticationEnvironment(url, token);
        Assert.Equal(count, environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("/dev/null", environment["GIT_CONFIG_GLOBAL"]);
        if (count == "0") Assert.DoesNotContain("GIT_CONFIG_VALUE_0", environment.Keys);
    }

    [Fact]
    public async Task GitReadsScopedHeaderWithoutPersistingItOrLoggingCredential()
    {
        const string token = "private-test-token";
        const string repository = "https://github.com/owner/private.git";
        var environment = GitRepository.AuthenticationEnvironment(repository, token);
        Assert.Equal("http.https://github.com/owner/private.git/.extraHeader", environment["GIT_CONFIG_KEY_0"]);
        var runner = new ProcessRunner();
        var header = await runner.RunAsync("git", ["config", "--get-urlmatch", "http.extraHeader", repository],
            null, _ => Task.CompletedTask, CancellationToken.None, false, environment);
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token)), header);
        var logs = new List<string>();
        var output = await GitRepository.RunAsync(runner, repository, token,
            ["config", "--get-urlmatch", "http.extraHeader", repository],
            line => { logs.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Authorization: Basic [REDACTED]", output);
        Assert.All(logs, line => Assert.DoesNotContain(token, line));
    }
}
