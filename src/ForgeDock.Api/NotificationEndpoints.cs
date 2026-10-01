using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class NotificationEndpoints
{
    /// <summary>
    /// Registers validated notification settings with encrypted webhook URLs and bounded delivery history;
    /// secret URLs are not returned.
    /// </summary>
    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(
            "/projects/{id:guid}/notifications",
            async (Guid id, ForgeDockDbContext db, IConfiguration config, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                var settings =
                    await db.NotificationSettings.FindAsync([id], ct)
                    ?? new NotificationSettings { ProjectId = id };
                var deliveries = await db
                    .NotificationDeliveries.AsNoTracking()
                    .Where(n => n.ProjectId == id)
                    .OrderByDescending(n => n.NextAttemptAt)
                    .Take(20)
                    .Select(n => new
                    {
                        n.Id,
                        n.Channel,
                        n.Event,
                        n.Attempts,
                        n.SentAt,
                        n.Error,
                    })
                    .ToListAsync(ct);
                return Results.Ok(
                    new
                    {
                        settings.OnSuccess,
                        settings.OnFailure,
                        settings.Email,
                        slackConfigured = settings.ProtectedSlackUrl.Length > 0,
                        discordConfigured = settings.ProtectedDiscordUrl.Length > 0,
                        smtpConfigured = !string.IsNullOrWhiteSpace(config["ForgeDock:Smtp:Host"]),
                        deliveries,
                    }
                );
            }
        );
        api.MapPut(
            "/projects/{id:guid}/notifications",
            async (
                Guid id,
                NotificationRequest request,
                ForgeDockDbContext db,
                SecretProtector protector,
                CancellationToken ct
            ) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                if (
                    !NotificationPolicy.ValidEmail(request.Email)
                    || request.SlackUrl is not null
                        && !NotificationPolicy.ValidWebhook("Slack", request.SlackUrl)
                    || request.DiscordUrl is not null
                        && !NotificationPolicy.ValidWebhook("Discord", request.DiscordUrl)
                )
                    return Results.Problem(
                        "Enter a valid email address or an official Slack/Discord HTTPS webhook URL.",
                        statusCode: 400
                    );
                var settings = await db.NotificationSettings.FindAsync([id], ct);
                if (settings is null)
                {
                    settings = new NotificationSettings { ProjectId = id };
                    db.NotificationSettings.Add(settings);
                }
                settings.OnSuccess = request.OnSuccess;
                settings.OnFailure = request.OnFailure;
                settings.Email = request.Email;
                if (request.SlackUrl is not null)
                    settings.ProtectedSlackUrl =
                        request.SlackUrl.Length == 0 ? "" : protector.Protect(request.SlackUrl);
                if (request.DiscordUrl is not null)
                    settings.ProtectedDiscordUrl =
                        request.DiscordUrl.Length == 0 ? "" : protector.Protect(request.DiscordUrl);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
        );
    }
}

public sealed record NotificationRequest(
    bool OnSuccess,
    bool OnFailure,
    string Email = "",
    string? SlackUrl = null,
    string? DiscordUrl = null
);
