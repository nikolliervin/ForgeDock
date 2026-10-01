import { useEffect, useState } from 'react';
import { CopyButton, Skeleton, useUI } from './ui';
import './webhooks.css';
import { navigate } from './navigation';

import type { Api } from './types';
type Settings = {
  enabled: boolean;
  configured: boolean;
  path: string;
  publicUrl: string | null;
  secret: string | null;
  lastDelivery: {
    receivedAt: string;
    status: string;
    event: string;
    deploymentId: string | null;
  } | null;
};
const deliveryLabels: Record<string, string> = {
  Queued: 'Deployment queued',
  Ping: 'GitHub connection verified',
  IgnoredBranch: 'Ignored: different branch or tag',
  IgnoredRepository: 'Ignored: different repository',
  IgnoredDeletedRef: 'Ignored: branch deleted',
  IgnoredDisabled: 'Ignored: auto-deploy disabled',
  IgnoredEvent: 'Ignored: event is not a push',
};

export function WebhookSettings({
  projectId,
  branch,
  api,
}: {
  projectId: string;
  branch: string;
  api: Api;
}) {
  const storageKey = `forgedock-webhook-url:${projectId}`;
  const [urlOverride, setUrlOverride] = useState<string | null>(() => {
    try {
      return localStorage.getItem(storageKey);
    } catch {
      return null;
    }
  });
  const [urlError, setUrlError] = useState('');
  const [settings, setSettings] = useState<Settings | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  const { confirm } = useUI();
  useEffect(() => {
    let active = true;
    async function refresh() {
      try {
        const result = await api<Settings>(`/projects/${projectId}/webhook`);
        if (active) {
          setSettings(result);
          setError('');
        }
      } catch (e) {
        if (active)
          setError(e instanceof Error ? e.message : 'Unable to load auto-deploy settings.');
      }
    }
    void refresh();
    const timer = setInterval(() => void refresh(), 10000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [projectId, api]);
  async function change(rotate = false) {
    if (!settings || busy) return;
    if (
      rotate &&
      !(await confirm({
        title: 'Rotate webhook secret?',
        message:
          'Update the secret in GitHub after rotating it. Deliveries signed with the previous secret will be rejected.',
        label: 'Rotate secret',
      }))
    )
      return;
    setBusy(true);
    setError('');
    try {
      const result = await api<Settings>(
        `/projects/${projectId}/webhook${rotate ? '/rotate-secret' : ''}`,
        rotate ? {} : { enabled: !settings.enabled },
        rotate ? 'POST' : 'PUT',
      );
      setSettings(result);
      if (result.secret) setSecret(result.secret);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to update auto-deploy settings.');
    } finally {
      setBusy(false);
    }
  }
  const path = settings?.path ?? `/api/webhooks/github/${projectId}`;
  const url = urlOverride ?? settings?.publicUrl ?? window.location.origin + path;
  function finishUrlEdit() {
    if (urlOverride === null) return;
    if (!urlOverride.trim()) {
      setUrlOverride(null);
      setUrlError('');
      try {
        localStorage.removeItem(storageKey);
      } catch {
        /* Browser storage is optional. */
      }
      return;
    }
    try {
      const address = new URL(
        urlOverride.includes('://') ? urlOverride.trim() : 'https://' + urlOverride.trim(),
      );
      if (
        address.protocol !== 'https:' ||
        address.username ||
        address.password ||
        address.search ||
        address.hash
      )
        throw new Error(
          'Use a public HTTPS URL without credentials, query parameters, or a fragment.',
        );
      const complete = address.pathname === '/' ? address.origin + path : address.href;
      setUrlOverride(complete);
      setUrlError('');
      try {
        localStorage.setItem(storageKey, complete);
      } catch {
        /* Keep the URL available for this page. */
      }
    } catch (e) {
      setUrlError(e instanceof Error ? e.message : 'Enter a valid public HTTPS URL.');
    }
  }
  return (
    <section className="webhook-settings" aria-labelledby="webhook-heading">
      <div className="section-heading">
        <h3 id="webhook-heading">GitHub auto-deploy</h3>
        {settings && (
          <span className={'webhook-badge ' + (settings.enabled ? 'enabled' : '')}>
            {settings.enabled ? 'Enabled' : 'Disabled'}
          </span>
        )}
      </div>
      <p>
        Automatically deploy commits pushed to <code>{branch}</code>. Every matching push uses your
        saved project settings and environment variables.
      </p>
      {error && (
        <p className="webhook-error" role="alert">
          {error}
        </p>
      )}
      {!settings ? (
        !error && <Skeleton label="Loading auto-deploy settings…" rows={2} />
      ) : (
        <>
          <div className="actions">
            <button
              type="button"
              className={settings.enabled ? 'secondary' : ''}
              disabled={busy}
              onClick={() => void change()}
            >
              {settings.enabled ? 'Disable auto-deploy' : 'Enable auto-deploy'}
            </button>
            {settings.configured && (
              <button
                type="button"
                className="secondary"
                disabled={busy}
                onClick={() => void change(true)}
              >
                Rotate webhook secret
              </button>
            )}
          </div>
          {settings.configured && (
            <div className="webhook-setup">
              <label>
                Webhook payload URL
                <div className="webhook-copy-row">
                  <input
                    aria-label="Webhook payload URL"
                    type="url"
                    value={url}
                    aria-invalid={!!urlError}
                    onChange={(event) => {
                      setUrlOverride(event.target.value);
                      setUrlError('');
                    }}
                    onBlur={finishUrlEdit}
                  />
                  <CopyButton value={url} label="webhook URL" />
                </div>
              </label>
              <p className="field-hint">
                Paste your Cloudflare or ngrok address here; the webhook path is added when you
                leave the field. This setup URL is remembered in this browser. Copy it into GitHub
                after changing it.
              </p>
              {urlError && (
                <p className="webhook-error" role="alert">
                  {urlError}
                </p>
              )}
              {!settings.publicUrl && (
                <p className="field-hint">
                  GitHub needs a public HTTPS address for this URL. Localhost cannot receive GitHub
                  deliveries.{' '}
                  <a
                    href="/docs/github-webhooks#cloudflare"
                    onClick={(event) => navigate(event, '/docs/github-webhooks#cloudflare')}
                  >
                    See public webhook setup →
                  </a>
                </p>
              )}
              <ol>
                <li>
                  In your GitHub repository, open <strong>Settings → Webhooks → Add webhook</strong>
                  .
                </li>
                <li>
                  Enter the payload URL above, choose <strong>application/json</strong>, and paste
                  the webhook secret.
                </li>
                <li>
                  Select <strong>Just the push event</strong>, leave SSL verification enabled, and
                  save.
                </li>
              </ol>
              {secret ? (
                <div className="webhook-secret">
                  <label>
                    Webhook secret
                    <div className="webhook-copy-row">
                      <input
                        aria-label="Webhook secret"
                        readOnly
                        value={secret}
                        autoComplete="off"
                      />
                      <CopyButton value={secret} label="webhook secret" />
                    </div>
                  </label>
                  <p>
                    Copy this secret into GitHub now. It is shown once and cannot be retrieved after
                    you leave this page.
                  </p>
                  <button type="button" className="secondary" onClick={() => setSecret(null)}>
                    Hide secret
                  </button>
                </div>
              ) : (
                <p className="field-hint">
                  If you no longer have the secret, rotate it and update your GitHub webhook.
                </p>
              )}
              <div className="webhook-delivery" role="status">
                {settings.lastDelivery ? (
                  <>
                    <span className="caption">LAST DELIVERY</span>
                    <strong>
                      {deliveryLabels[settings.lastDelivery.status] ?? settings.lastDelivery.status}
                    </strong>
                    <span>
                      {new Date(settings.lastDelivery.receivedAt).toLocaleString()} ·{' '}
                      {settings.lastDelivery.event}
                    </span>
                    {settings.lastDelivery.deploymentId && (
                      <a
                        href={`/projects/${projectId}/deployments`}
                        onClick={(event) => navigate(event, `/projects/${projectId}/deployments`)}
                      >
                        View deployment history →
                      </a>
                    )}
                  </>
                ) : (
                  <>
                    <strong>Waiting for GitHub</strong>
                    <span>
                      Save the webhook in GitHub to send a connection check, then push a commit to{' '}
                      {branch}.
                    </span>
                  </>
                )}
              </div>
            </div>
          )}
        </>
      )}
    </section>
  );
}
