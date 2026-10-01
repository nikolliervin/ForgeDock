import { useEffect, useState } from 'react';
import { useUI } from './ui';
import { databaseNames, type Database } from './Databases';
import type { Api } from './types';
type Backup = {
  id: string;
  serviceId: string;
  kind: string;
  state: string;
  error: string | null;
  sizeBytes: number;
  createdAt: string;
  remoteState?: string;
  remoteError?: string | null;
};
export function Backups({ projectId, api }: { projectId: string; api: Api }) {
  const [jobs, setJobs] = useState<Backup[]>([]),
    [services, setServices] = useState<Database[]>([]),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  const { confirm } = useUI();
  async function refresh() {
    try {
      const [backups, databases] = await Promise.all([
        api<Backup[]>(`/projects/${projectId}/backups`),
        api<Database[]>(`/projects/${projectId}/databases`),
      ]);
      setJobs(backups);
      setServices(databases);
    } catch (e) {
      setError((e as Error).message);
    }
  }
  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 4000);
    return () => clearInterval(timer);
  }, [projectId, api]);
  async function run(path: string, body = {}, method = 'POST') {
    setBusy(true);
    setError('');
    try {
      await api(`/projects/${projectId}/${path}`, body, method);
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const pending = jobs.some((job) => ['Queued', 'Running'].includes(job.state));
  return (
    <section className="panel form">
      <h2>Backups and restore</h2>
      <p>
        Backups are encrypted before storage. When S3 storage is configured, completed backups
        upload automatically and can be restored even after their local files are removed. Retention
        is configured by your operator. Preserve the platform secret key and control database
        separately. MongoDB backups briefly pause the app for a consistent snapshot.
      </p>
      {error && <p role="alert">{error}</p>}
      {!services.length && <p>Add a database in the Databases tab first.</p>}
      {services.map((service) => (
        <section key={service.id}>
          <h3>{databaseNames[service.kind]}</h3>
          <div className="actions">
            <button
              disabled={busy || pending || service.state !== 'Running'}
              onClick={() => void run(`databases/${service.id}/backups`)}
            >
              Back up now
            </button>
            <label>
              Automatic backups
              <select
                aria-label={`${service.kind} backup schedule`}
                value={service.backupIntervalHours ?? 0}
                disabled={busy}
                onChange={(event) =>
                  void run(
                    `databases/${service.id}/schedule`,
                    { intervalHours: Number(event.target.value) },
                    'PUT',
                  )
                }
              >
                <option value={0}>Disabled</option>
                <option value={1}>Hourly</option>
                <option value={6}>Every 6 hours</option>
                <option value={24}>Daily</option>
                <option value={168}>Weekly</option>
              </select>
            </label>
          </div>
          {service.nextBackupAt && (
            <p className="field-hint">
              Next backup: {new Date(service.nextBackupAt).toLocaleString()}
            </p>
          )}
        </section>
      ))}
      <h3>Recent operations</h3>
      {jobs.map((job) => (
        <article key={job.id}>
          <p>
            <strong>
              {job.kind} · {job.state}
            </strong>{' '}
            ·{' '}
            {(() => {
              const service = services.find((service) => service.id === job.serviceId);
              return service ? databaseNames[service.kind] : 'Database';
            })()}{' '}
            · {new Date(job.createdAt).toLocaleString()}
            {job.sizeBytes > 0 && ` · ${(job.sizeBytes / 1024).toFixed(1)} KiB`}
          </p>
          {job.kind === 'Backup' && (
            <p className="field-hint">Remote storage: {job.remoteState ?? 'Disabled'}</p>
          )}
          {job.remoteError && <p role="alert">{job.remoteError}</p>}
          {job.error && <p role="alert">{job.error}</p>}
          {job.kind === 'Backup' && job.state === 'Completed' && (
            <button
              className="secondary"
              disabled={busy || pending}
              onClick={async () => {
                if (
                  await confirm({
                    title: 'Restore database?',
                    message:
                      'This replaces current database data with this backup. The app will stop temporarily and restart afterward.',
                    label: 'Restore database',
                    danger: true,
                  })
                )
                  await run(`backups/${job.id}/restore`, { confirm: true });
              }}
            >
              Restore this backup
            </button>
          )}
        </article>
      ))}
    </section>
  );
}
