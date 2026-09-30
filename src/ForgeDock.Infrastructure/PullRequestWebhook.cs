using System.Text.Json;
using ForgeDock.Application;
namespace ForgeDock.Infrastructure;
public sealed record PullRequestEvent(string Status, int Number, string? Branch, string? Sha, DateTimeOffset UpdatedAt);
public static class PullRequestWebhook
{
    public static PullRequestEvent Evaluate(ReadOnlyMemory<byte> body, string repositoryUrl, string branch)
    {
        using var json = JsonDocument.Parse(body); var root = json.RootElement; var identity = GitHubWebhook.RepositoryIdentity(repositoryUrl);
        if (identity is null || GitHubWebhook.RepositoryIdentity(root.GetProperty("repository").GetProperty("html_url").GetString() ?? "") != identity)
            return new("IgnoredRepository", 0, null, null, default);
        var number = root.GetProperty("number").GetInt32(); if (number <= 0) throw new FormatException();
        var pr = root.GetProperty("pull_request"); var updated = pr.GetProperty("updated_at").GetDateTimeOffset();
        var action = root.GetProperty("action").GetString();
        if (action == "closed") return new("PreviewClosed", number, null, null, updated);
        if (action is not ("opened" or "reopened" or "synchronize" or "ready_for_review")) return new("IgnoredAction", number, null, null, updated);
        if (pr.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return new("IgnoredDraft", number, null, null, updated);
        if (pr.GetProperty("base").GetProperty("ref").GetString() != branch) return new("IgnoredBranch", number, null, null, updated);
        var head = pr.GetProperty("head"); var repo = head.GetProperty("repo");
        if (repo.ValueKind != JsonValueKind.Object || GitHubWebhook.RepositoryIdentity(repo.GetProperty("html_url").GetString() ?? "") != identity)
            return new("IgnoredFork", number, null, null, updated);
        var sha = head.GetProperty("sha").GetString(); var reference = head.GetProperty("ref").GetString();
        if (sha is null || !ProjectConfiguration.IsCommitSha(sha) || string.IsNullOrWhiteSpace(reference)) throw new FormatException();
        return new("PreviewQueued", number, reference, sha.ToLowerInvariant(), updated);
    }
}
