import { useEffect, useState } from 'react';
import type { Api } from './types';
type Policy = {
  autoRollbackEnabled: boolean;
  rollbackWindowMinutes: number;
  rollbackFailureThreshold: number;
};
type Release = {
  id: string;
  rollbackDeadlineAt: string;
  healthFailureCount: number;
  autoRollbackTriggeredAt: string | null;
};
export function AutomaticRollback({ projectId, api }: { projectId: string; api: Api }) {
  const [policy, setPolicy] = useState<Policy>({
      autoRollbackEnabled: false,
      rollbackWindowMinutes: 10,
      rollbackFailureThreshold: 3,
    }),
    [releases, setReleases] = useState<Release[]>([]),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  useEffect(() => {
    void api<Policy & { releases: Release[] }>(`/projects/${projectId}/rollback-policy`)
      .then((data) => {
        setPolicy({
          autoRollbackEnabled: data.autoRollbackEnabled ?? false,
          rollbackWindowMinutes: data.rollbackWindowMinutes || 10,
          rollbackFailureThreshold: data.rollbackFailureThreshold || 3,
        });
        setReleases(data.releases ?? []);
      })
      .catch((e) => setError(e.message));
  }, [projectId, api]);
  return (
    <section className="panel form settings-card">
      <h3>Automatic rollback</h3>
      <p>
        Observe new releases and restore the previous image after repeated failed health checks.
        Changes apply to future deployments. Database changes are not reverted.
      </p>
      {error && <p role="alert">{error}</p>}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          setBusy(true);
          setError('');
          void api(`/projects/${projectId}/rollback-policy`, policy, 'PUT')
            .catch((e) => setError(e.message))
            .finally(() => setBusy(false));
        }}
      >
        <label>
          <input
            type="checkbox"
            checked={policy.autoRollbackEnabled}
            onChange={(e) => setPolicy({ ...policy, autoRollbackEnabled: e.target.checked })}
          />
          Enable automatic rollback
        </label>
        <label>
          Observation window (minutes)
          <input
            type="number"
            min={1}
            max={60}
            required
            value={policy.rollbackWindowMinutes}
            onChange={(e) =>
              setPolicy({ ...policy, rollbackWindowMinutes: Number(e.target.value) })
            }
          />
        </label>
        <label>
          Consecutive failed health checks
          <input
            type="number"
            min={1}
            max={10}
            required
            value={policy.rollbackFailureThreshold}
            onChange={(e) =>
              setPolicy({ ...policy, rollbackFailureThreshold: Number(e.target.value) })
            }
          />
        </label>
        <button disabled={busy}>Save rollback policy</button>
      </form>
      {releases.map((release) => (
        <p key={release.id}>
          <a href={`/projects/${projectId}/deployments?deployment=${release.id}`}>
            {release.id.slice(0, 8)}
          </a>{' '}
          · {release.healthFailureCount} failed checks ·{' '}
          {release.autoRollbackTriggeredAt
            ? 'Rollback queued'
            : `Observed until ${new Date(release.rollbackDeadlineAt).toLocaleString()}`}
        </p>
      ))}
    </section>
  );
}
