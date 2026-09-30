using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Worker;

public sealed class NotificationWorker(IServiceScopeFactory scopes, IConfiguration configuration, SecretProtector protector,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessAsync(ct); }
            catch (Exception error) when (!ct.IsCancellationRequested) { logger.LogWarning("Notification processing failed ({Type}).", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
    public async Task ProcessAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(74623021) AS \"Value\"").SingleAsync(ct)) return;
        foreach (var settings in await db.NotificationSettings.ToListAsync(ct))
        {
            var deployments = await db.Deployments.Where(d => d.ProjectId == settings.ProjectId && d.CreatedAt >= settings.EnabledAt &&
                (d.State == DeploymentState.Running || d.State == DeploymentState.Failed || d.State == DeploymentState.Stopped)).ToListAsync(ct);
            var name = (await db.Projects.FindAsync([settings.ProjectId], ct))!.Name;
            foreach (var deployment in deployments)
            {
                var success = deployment.State != DeploymentState.Failed;
                if (success ? !settings.OnSuccess : !settings.OnFailure) continue;
                var eventName = success ? "DeploymentSucceeded" : "DeploymentFailed";
                var message = $"{name}: deployment {(success ? "succeeded" : "failed")}.\n" + NotificationPolicy.LogLink(configuration["ForgeDock:DashboardBaseUrl"] ?? "http://localhost:5173", settings.ProjectId, deployment.Id);
                foreach (var channel in new[] { "Slack", "Discord", "Email" })
                    if (Configured(settings, channel) && !await db.NotificationDeliveries.AnyAsync(n => n.DeploymentId == deployment.Id && n.Event == eventName && n.Channel == channel, ct))
                        db.NotificationDeliveries.Add(new NotificationDelivery { ProjectId = settings.ProjectId, DeploymentId = deployment.Id, Event = eventName, Channel = channel, Message = message });
            }
        }
        await db.SaveChangesAsync(ct);
        var cutoff = DateTimeOffset.UtcNow;
        foreach (var delivery in await db.NotificationDeliveries.Where(n => n.SentAt == null && n.Attempts < 5 && n.NextAttemptAt <= cutoff).OrderBy(n => n.NextAttemptAt).Take(20).ToListAsync(ct))
        {
            var settings = await db.NotificationSettings.FindAsync([delivery.ProjectId], ct);
            delivery.Attempts++;
            try
            {
                if (settings is null || !Configured(settings, delivery.Channel)) throw new InvalidOperationException();
                await Send(settings, delivery, ct); delivery.SentAt = DateTimeOffset.UtcNow; delivery.Error = null;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Provider exceptions can contain webhook tokens or SMTP credentials.
                delivery.Error = "Delivery failed. Check channel credentials and connectivity.";
                delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, delivery.Attempts));
            }
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    private static bool Configured(NotificationSettings settings, string channel) => channel switch
    { "Slack" => settings.ProtectedSlackUrl.Length > 0, "Discord" => settings.ProtectedDiscordUrl.Length > 0, "Email" => settings.Email.Length > 0, _ => false };
    private async Task Send(NotificationSettings settings, NotificationDelivery delivery, CancellationToken ct)
    {
        if (delivery.Channel == "Email")
        {
            using var client = new SmtpClient(configuration["ForgeDock:Smtp:Host"] ?? throw new InvalidOperationException(),
                configuration.GetValue("ForgeDock:Smtp:Port", 587)) { EnableSsl = configuration.GetValue("ForgeDock:Smtp:EnableSsl", true), Timeout = 15000 };
            if (configuration["ForgeDock:Smtp:Username"] is { Length: > 0 } username)
                client.Credentials = new NetworkCredential(username, configuration["ForgeDock:Smtp:Password"]);
            using var mail = new MailMessage(configuration["ForgeDock:Smtp:From"] ?? throw new InvalidOperationException(), settings.Email,
                "ForgeDock deployment notification", delivery.Message);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await client.SendMailAsync(mail, deadline.Token); return;
        }
        var url = protector.Unprotect(delivery.Channel == "Slack" ? settings.ProtectedSlackUrl : settings.ProtectedDiscordUrl);
        if (!NotificationPolicy.ValidWebhook(delivery.Channel, url)) throw new InvalidOperationException();
        using var clientHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        object payload = delivery.Channel == "Slack" ? new { text = delivery.Message, mrkdwn = false } :
            new { content = delivery.Message, allowed_mentions = new { parse = Array.Empty<string>() } };
        using var response = await clientHttp.PostAsJsonAsync(url + (delivery.Channel == "Discord" ? "?wait=true" : ""), payload, ct);
        response.EnsureSuccessStatusCode();
    }
}
