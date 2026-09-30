# Deployment notifications

Open a project's **Settings → Deployment notifications**. Choose successes, failures, or both and configure one or more channels.

- Slack: create an incoming webhook for a channel in your Slack workspace, then paste its `https://hooks.slack.com/services/...` URL.
- Discord: in a channel's **Edit Channel → Integrations → Webhooks**, create a webhook and copy its URL.
- Email: enter one recipient. The operator must configure `ForgeDock__Smtp__Host`, `Port` (default 587), `From`, `Username`, `Password`, and `EnableSsl` (default true). Use your existing SMTP server or a provider's free allowance; ForgeDock does not require a paid notification service.

Webhook URLs are encrypted with the platform secret key and are never returned to the browser. Empty webhook inputs preserve the current connection; use Disconnect to remove it. Email recipients are visible to authenticated operators.

Set `ForgeDock__DashboardBaseUrl` to your dashboard address. Local development defaults to `http://localhost:5173`. Messages link to the exact deployment's history and logs; dashboard authentication is still required. A Cloudflare tunnel exposing only the GitHub receiver is not a public dashboard address.

The worker discovers terminal deployments created after notification settings were first saved. Deliveries are persisted and retried up to five times with increasing delays; recent status appears below settings. Notifications never change deployment results. Delivery is at least once: a worker interruption after a provider accepts a message can result in a duplicate on retry. No notification is sent for cancelled deployments.

Sources: [Slack incoming webhooks](https://api.slack.com/messaging/webhooks), [Discord webhooks](https://docs.discord.com/developers/resources/webhook).
