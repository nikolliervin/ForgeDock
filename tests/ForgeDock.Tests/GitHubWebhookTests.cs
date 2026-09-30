using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public sealed class GitHubWebhookTests
{
    [Fact]
    public void MatchesGitHubPublishedSignatureVector()
    {
        // https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries
        Assert.True(GitHubWebhook.VerifySignature(Encoding.UTF8.GetBytes("Hello, World!"), "It's a Secret to Everybody",
            "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17"));
    }

    [Fact]
    public void VerifiesRawUtf8AndRejectsTampering()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"Ship 🚀\"}\n");
        const string secret = "per-project-secret";
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        Assert.True(GitHubWebhook.VerifySignature(body, secret, signature));
        Assert.False(GitHubWebhook.VerifySignature(body[..^1], secret, signature));
        Assert.False(GitHubWebhook.VerifySignature(body, "different-secret", signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=not-a-hash")]
    public void RejectsMissingOrMalformedSignature(string? signature) =>
        Assert.False(GitHubWebhook.VerifySignature("{}"u8, "secret", signature));

    private static byte[] Push(string reference = "refs/heads/main", bool deleted = false, string repo = "https://github.com/Owner/App", string? sha = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { @ref = reference, deleted, after = sha ?? new string('a', 40), repository = new { html_url = repo } });

    [Fact]
    public void PinsExactPushedCommitAndNormalizesRepository()
    {
        var (status, sha) = GitHubWebhook.EvaluatePush(Push(sha: new string('B', 40)), "https://github.com/owner/app.git", "main");
        Assert.Equal("Queued", status); Assert.Equal(new string('b', 40), sha);
    }

    [Theory]
    [InlineData("refs/heads/other", false, "https://github.com/Owner/App", "IgnoredBranch")]
    [InlineData("refs/tags/main", false, "https://github.com/Owner/App", "IgnoredBranch")]
    [InlineData("refs/heads/main", true, "https://github.com/Owner/App", "IgnoredDeletedRef")]
    [InlineData("refs/heads/main", false, "https://github.com/Other/App", "IgnoredRepository")]
    [InlineData("refs/heads/main", false, "https://github.com.evil.test/Owner/App", "IgnoredRepository")]
    public void IgnoresNonMatchingPushes(string reference, bool deleted, string repo, string expected)
    {
        var result = GitHubWebhook.EvaluatePush(Push(reference, deleted, repo), "https://github.com/owner/app", "main");
        Assert.Equal(expected, result.Status); Assert.Null(result.CommitSha);
    }

    [Fact]
    public void SupportsBranchNamesWithSlashesAndRejectsInvalidCommit()
    {
        Assert.Equal("Queued", GitHubWebhook.EvaluatePush(Push("refs/heads/release/v2"), "https://github.com/owner/app", "release/v2").Status);
        Assert.Throws<FormatException>(() => GitHubWebhook.EvaluatePush(Push(sha: "not-a-sha"), "https://github.com/owner/app", "main"));
        Assert.Equal("IgnoredDeletedRef", GitHubWebhook.EvaluatePush(Push(sha: new string('0', 40)), "https://github.com/owner/app", "main").Status);
    }
}
