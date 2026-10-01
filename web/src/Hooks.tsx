import { useEffect, useState } from 'react';
import type { Api } from './types';
type Settings = { preDeployCommand: string; postDeployCommand: string; hookTimeoutSeconds: number };
type Execution = {
  id: string;
  deploymentId: string;
  phase: string;
  state: string;
  output: string;
  exitCode: number | null;
  truncated: boolean;
  startedAt: string;
};
export function Hooks({ projectId, api }: { projectId: string; api: Api }) {
  const [settings, setSettings] = useState<Settings>({
      preDeployCommand: '',
      postDeployCommand: '',
      hookTimeoutSeconds: 120,
    }),
    [executions, setExecutions] = useState<Execution[]>([]),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  useEffect(() => {
    let active = true;
    void api<Settings & { executions: Execution[] }>(`/projects/${projectId}/hooks`)
      .then((data) => {
        if (active) {
          setSettings({
            preDeployCommand: data.preDeployCommand ?? '',
            postDeployCommand: data.postDeployCommand ?? '',
            hookTimeoutSeconds: data.hookTimeoutSeconds ?? 120,
          });
          setExecutions(data.executions ?? []);
        }
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [projectId, api]);
  return (
    <section className="panel form settings-card">
      <h3>Deployment hooks</h3>
      <p>
        Commands run in the candidate app container before and after routing. Use environment
        variables for credentials. Images need /bin/sh and timeout. Rollback and restart skip hooks.
      </p>
      {error && <p role="alert">{error}</p>}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          setBusy(true);
          setError('');
          void api(`/projects/${projectId}/hooks`, settings, 'PUT')
            .catch((e) => setError(e.message))
            .finally(() => setBusy(false));
        }}
      >
        <label>
          Before routing command
          <textarea
            maxLength={4096}
            value={settings.preDeployCommand}
            onChange={(e) => setSettings({ ...settings, preDeployCommand: e.target.value })}
            placeholder="npm run migrate"
          />
        </label>
        <label>
          After routing command
          <textarea
            maxLength={4096}
            value={settings.postDeployCommand}
            onChange={(e) => setSettings({ ...settings, postDeployCommand: e.target.value })}
          />
        </label>
        <label>
          Hook timeout (seconds)
          <input
            type="number"
            min={1}
            max={900}
            required
            value={settings.hookTimeoutSeconds}
            onChange={(e) =>
              setSettings({ ...settings, hookTimeoutSeconds: Number(e.target.value) })
            }
          />
        </label>
        <button disabled={busy}>Save deployment hooks</button>
      </form>
      {executions.map((hook) => (
        <details key={hook.id}>
          <summary>
            {hook.phase} · {hook.state} · exit {hook.exitCode ?? 'unavailable'} ·{' '}
            {new Date(hook.startedAt).toLocaleString()}
          </summary>
          <a href={`/projects/${projectId}/deployments?deployment=${hook.deploymentId}`}>
            Deployment details
          </a>
          <pre>{hook.output || 'No output'}</pre>
          {hook.truncated && <p>Output was truncated.</p>}
        </details>
      ))}
    </section>
  );
}
