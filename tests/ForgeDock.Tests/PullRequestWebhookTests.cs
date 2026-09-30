using System.Text.Json;
using ForgeDock.Infrastructure;
namespace ForgeDock.Tests;
public class PullRequestWebhookTests
{
    private static byte[] Payload(string action = "opened", string repo = "https://github.com/team/app", string headRepo = "https://github.com/team/app", string branch = "main", bool draft = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        action, number = 7, repository = new { html_url = repo }, pull_request = new { updated_at = "2026-10-01T00:00:00Z", draft,
            @base = new { @ref = branch }, head = new { @ref = "feature/example", sha = new string('a', 40), repo = new { html_url = headRepo } } }
    });
    [Fact]
    public void QueuesExactHeadForTrustedRepository()
    { var result = PullRequestWebhook.Evaluate(Payload(), "https://github.com/TEAM/app.git", "main"); Assert.Equal("PreviewQueued", result.Status); Assert.Equal(7, result.Number); Assert.Equal(new string('a', 40), result.Sha); Assert.Equal("feature/example", result.Branch); }
    [Theory]
    [InlineData("opened", "https://github.com/fork/app", "main", false, "IgnoredFork")]
    [InlineData("opened", "https://github.com/team/app", "other", false, "IgnoredBranch")]
    [InlineData("opened", "https://github.com/team/app", "main", true, "IgnoredDraft")]
    [InlineData("edited", "https://github.com/team/app", "main", false, "IgnoredAction")]
    [InlineData("closed", "https://github.com/fork/app", "other", true, "PreviewClosed")]
    public void FiltersAndCloses(string action, string head, string branch, bool draft, string status) => Assert.Equal(status, PullRequestWebhook.Evaluate(Payload(action, headRepo: head, branch: branch, draft: draft), "https://github.com/team/app", "main").Status);
    [Fact]
    public void RejectsDifferentParentRepository() => Assert.Equal("IgnoredRepository", PullRequestWebhook.Evaluate(Payload(repo: "https://github.com/other/app"), "https://github.com/team/app", "main").Status);
}
