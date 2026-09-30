import { useEffect, useState } from 'react';
type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
type Settings = { onSuccess: boolean; onFailure: boolean; email: string; slackConfigured: boolean; discordConfigured: boolean; smtpConfigured: boolean; deliveries: { id: string; channel: string; event: string; attempts: number; sentAt: string | null; error: string | null }[] };
export function Notifications({ projectId, api }: { projectId: string; api: Api }) {
  const [settings, setSettings] = useState<Settings | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  async function refresh() { try { setSettings(await api<Settings>(`/projects/${projectId}/notifications`)); } catch (e) { setError((e as Error).message); } }
  useEffect(() => { void refresh(); const timer = setInterval(() => void refresh(), 10000); return () => clearInterval(timer); }, [projectId, api]);
  return <section><h3>Deployment notifications</h3><p>Send results through free Slack or Discord incoming webhooks, or your existing SMTP server. Messages include a link to deployment logs.</p>{error && <p role="alert">{error}</p>}{settings && <form onSubmit={async event => {
    event.preventDefault(); const form = event.currentTarget, data = new FormData(form); setBusy(true); setError('');
    try { await api(`/projects/${projectId}/notifications`, { onSuccess: data.has('success'), onFailure: data.has('failure'), email: data.get('email'),
      slackUrl: data.has('removeSlack') ? '' : String(data.get('slack') || '') || null, discordUrl: data.has('removeDiscord') ? '' : String(data.get('discord') || '') || null }, 'PUT'); form.reset(); await refresh(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }}>
    <label><input name="success" type="checkbox" defaultChecked={settings.onSuccess} /> Successful deployments</label>
    <label><input name="failure" type="checkbox" defaultChecked={settings.onFailure} /> Failed deployments</label>
    <label>Slack webhook {settings.slackConfigured && '· Connected'}<input name="slack" type="password" autoComplete="new-password" placeholder={settings.slackConfigured ? 'Leave empty to keep current webhook' : 'https://hooks.slack.com/services/…'} /></label>
    {settings.slackConfigured && <label><input type="checkbox" name="removeSlack" /> Disconnect Slack</label>}
    <label>Discord webhook {settings.discordConfigured && '· Connected'}<input name="discord" type="password" autoComplete="new-password" placeholder={settings.discordConfigured ? 'Leave empty to keep current webhook' : 'https://discord.com/api/webhooks/…'} /></label>
    {settings.discordConfigured && <label><input type="checkbox" name="removeDiscord" /> Disconnect Discord</label>}
    <label>Email recipient<input name="email" type="email" defaultValue={settings.email} placeholder="you@example.com" /></label>
    {!settings.smtpConfigured && <p className="field-hint">Email requires the operator to configure ForgeDock__Smtp__Host, From, and SMTP credentials. Slack and Discord need no server configuration.</p>}
    <button disabled={busy}>Save notifications</button>
  </form>}{settings?.deliveries?.length ? <><h4>Recent notifications</h4>{settings.deliveries.map(item => <p key={item.id}>{item.channel} · {item.event === 'DeploymentSucceeded' ? 'Deployment succeeded' : item.event === 'DeploymentFailed' ? 'Deployment failed' : item.event} · {item.sentAt ? 'Delivered' : item.attempts >= 5 ? 'Failed after 5 attempts' : item.attempts ? 'Retry scheduled' : 'Queued'}{item.error && <small> — {item.error}</small>}</p>)}</> : null}</section>;
}
