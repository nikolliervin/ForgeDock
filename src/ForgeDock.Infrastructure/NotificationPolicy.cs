using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ForgeDock.Infrastructure;

public static partial class NotificationPolicy
{
    /// <summary>
    /// Allows only supported Slack/Discord HTTPS webhook origins and path formats; arbitrary URLs and
    /// redirects must not become notification egress targets.
    /// </summary>
    public static bool ValidWebhook(string channel, string value)
    {
        if (value.Length == 0)
            return true;
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
            return false;
        return channel == "Slack"
            ? uri.Host == "hooks.slack.com" && SlackPath().IsMatch(uri.AbsolutePath)
            : channel == "Discord"
                && uri.Host == "discord.com"
                && DiscordPath().IsMatch(uri.AbsolutePath);
    }

    /// <summary>
    /// Allows an empty disabled recipient or a bounded, exact mailbox without header-injection characters.
    /// </summary>
    public static bool ValidEmail(string value) =>
        value.Length == 0
        || value.Length <= 254
            && MailAddress.TryCreate(value, out var mail)
            && mail.Address == value
            && !value.Contains('\n')
            && !value.Contains('\r');

    /// <summary>
    /// Builds a stable deployment deep link from a validated dashboard origin without embedded credentials
    /// or existing query strings.
    /// </summary>
    public static string LogLink(string origin, Guid project, Guid deployment)
    {
        if (
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
            throw new InvalidOperationException(
                "ForgeDock__DashboardBaseUrl must be an HTTP(S) dashboard address."
            );
        return origin.TrimEnd('/') + $"/projects/{project}/deployments?deployment={deployment}";
    }

    [GeneratedRegex(@"^/services/[A-Za-z0-9]+/[A-Za-z0-9]+/[A-Za-z0-9]+$")]
    private static partial Regex SlackPath();

    [GeneratedRegex(@"^/api(?:/v[0-9]+)?/webhooks/[0-9]+/[A-Za-z0-9_-]+$")]
    private static partial Regex DiscordPath();
}
