import { useState } from 'react';
import { Skeleton, useUI } from './ui';
import { usePollingResource } from './usePollingResource';
import type { Api } from './types';
import './automation.css';

type Job = {
  id: string;
  name: string;
  command: string;
  intervalMinutes: number;
  timeoutSeconds: number;
  enabled: boolean;
  nextRunAt: string | null;
  lastError: string | null;
};
type Run = {
  id: string;
  name: string;
  state: string;
  output: string;
  exitCode: number | null;
  truncated: boolean;
  createdAt: string;
  scheduledJobId?: string;
};
const initial = { name: '', command: '', intervalMinutes: 0, timeoutSeconds: 120, enabled: true };

export function Jobs({ projectId, api }: { projectId: string; api: Api }) {
  const path = `/projects/${projectId}/jobs`;
  const {
    data,
    loading,
    error: loadError,
    refresh,
  } = usePollingResource<{ jobs: Job[]; runs: Run[] }>(api, path, 4000);
  const [form, setForm] = useState(initial);
  const [editing, setEditing] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const { confirm } = useUI();
  const jobs = data?.jobs ?? [],
    runs = data?.runs ?? [];
  const linkedRun = new URLSearchParams(window.location.search).get('run');

  async function run(task: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await task();
      await refresh(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function reset() {
    setEditing(null);
    setForm(initial);
  }

  return (
    <div className="automation-page">
      <div className="automation-heading">
        <div>
          <h2>Scheduled jobs</h2>
          <p>Run maintenance commands and recurring tasks from your active release.</p>
        </div>
        <span className="badge">{jobs.length} tasks</span>
      </div>
      {(error || loadError) && (
        <p role="alert" className="error">
          {error || loadError}
        </p>
      )}
      <div className="automation-grid">
        <section className="panel form automation-form">
          <h3>{editing ? 'Edit task' : 'Create a task'}</h3>
          <p>
            Tasks use a separate container with the release's runtime environment and access to
            managed databases.
          </p>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void run(async () => {
                await api(`${path}${editing ? '/' + editing : ''}`, form, editing ? 'PUT' : 'POST');
                reset();
              });
            }}
          >
            <fieldset disabled={busy} className="automation-fields">
              <label>
                Job name
                <input
                  required
                  maxLength={80}
                  placeholder="Daily maintenance"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                />
              </label>
              <label>
                Task command
                <textarea
                  required
                  maxLength={4096}
                  rows={3}
                  placeholder="npm run maintenance"
                  value={form.command}
                  onChange={(e) => setForm({ ...form, command: e.target.value })}
                />
              </label>
              <label>
                Repeat interval (minutes)
                <input
                  type="number"
                  min={0}
                  max={10080}
                  required
                  value={form.intervalMinutes}
                  onChange={(e) => setForm({ ...form, intervalMinutes: Number(e.target.value) })}
                />
                <small>Use 0 for a task you run manually.</small>
              </label>
              <label>
                Task timeout (seconds)
                <input
                  type="number"
                  min={1}
                  max={900}
                  required
                  value={form.timeoutSeconds}
                  onChange={(e) => setForm({ ...form, timeoutSeconds: Number(e.target.value) })}
                />
              </label>
              <div className="actions">
                <button>{editing ? 'Save task changes' : 'Add task'}</button>
                {editing && (
                  <button type="button" className="secondary" onClick={reset}>
                    Cancel editing
                  </button>
                )}
              </div>
            </fieldset>
          </form>
          <p className="field-hint">
            Application volumes are not mounted. Your image needs <code>/bin/sh</code> and{' '}
            <code>timeout</code>.
          </p>
        </section>
        <div className="automation-content">
          <section className="panel">
            <div className="section-heading">
              <h3>Tasks</h3>
              <span className="count">{jobs.length}</span>
            </div>
            {loading ? (
              <Skeleton label="Loading tasks…" />
            ) : !jobs.length ? (
              <div className="empty compact">
                <h3>No tasks yet</h3>
                <p>Create a manual task or a recurring schedule to get started.</p>
              </div>
            ) : (
              jobs.map((job) => {
                const pending = runs.some(
                  (task) =>
                    task.scheduledJobId === job.id && ['Queued', 'Running'].includes(task.state),
                );
                return (
                  <article key={job.id} className="automation-task">
                    <div className="automation-task-heading">
                      <h4>{job.name}</h4>
                      <span className="badge">
                        {job.intervalMinutes ? (job.enabled ? 'Scheduled' : 'Paused') : 'Manual'}
                      </span>
                    </div>
                    <p>
                      {job.intervalMinutes
                        ? `Every ${job.intervalMinutes} minutes`
                        : 'Run on demand'}{' '}
                      · {job.timeoutSeconds}s timeout
                    </p>
                    {job.enabled && job.nextRunAt && (
                      <small>Next run: {new Date(job.nextRunAt).toLocaleString()}</small>
                    )}
                    {job.lastError && (
                      <p role="alert" className="error">
                        {job.lastError}
                      </p>
                    )}
                    <div className="actions">
                      <button
                        disabled={busy || pending}
                        onClick={() => void run(() => api(`${path}/${job.id}/run`, {}))}
                      >
                        Run {job.name} now
                      </button>
                      <button
                        className="secondary"
                        disabled={busy}
                        onClick={() => {
                          setEditing(job.id);
                          setForm({
                            name: job.name,
                            command: job.command,
                            intervalMinutes: job.intervalMinutes,
                            timeoutSeconds: job.timeoutSeconds,
                            enabled: job.enabled,
                          });
                        }}
                      >
                        Edit {job.name}
                      </button>
                      {job.intervalMinutes > 0 && (
                        <button
                          className="secondary"
                          disabled={busy}
                          onClick={() =>
                            void run(() =>
                              api(`${path}/${job.id}`, { ...job, enabled: !job.enabled }, 'PUT'),
                            )
                          }
                        >
                          {job.enabled ? 'Pause' : 'Enable'} {job.name}
                        </button>
                      )}
                      <button
                        className="danger"
                        disabled={busy || pending}
                        onClick={() =>
                          void run(async () => {
                            if (
                              await confirm({
                                title: 'Delete task?',
                                message:
                                  'Remove this schedule. Completed execution history remains.',
                                label: 'Delete task',
                                danger: true,
                              })
                            ) {
                              await api(`${path}/${job.id}`, undefined, 'DELETE');
                              if (editing === job.id) reset();
                            }
                          })
                        }
                      >
                        Delete {job.name}
                      </button>
                    </div>
                  </article>
                );
              })
            )}
          </section>
          <section className="panel">
            <div className="section-heading">
              <h3>Execution history</h3>
              <span className="count">{runs.length}</span>
            </div>
            {loading ? (
              <Skeleton label="Loading task history…" />
            ) : !runs.length ? (
              <div className="empty compact">
                <h3>No executions yet</h3>
                <p>Output, exit status, and execution time will appear here.</p>
              </div>
            ) : (
              runs.map((task) => (
                <details
                  className="automation-run"
                  key={task.id}
                  open={linkedRun === task.id || undefined}
                >
                  <summary>
                    {task.name} · {task.state} · exit {task.exitCode ?? 'pending'} ·{' '}
                    {new Date(task.createdAt).toLocaleString()}
                  </summary>
                  <pre>{task.output || 'No output yet'}</pre>
                  {task.truncated && <p className="field-hint">Output was truncated.</p>}
                </details>
              ))
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
