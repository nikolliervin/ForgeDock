import { useEffect, useState } from 'react';
import { useUI } from './ui';
type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
type Policy = { automaticCleanup: boolean; retainedDeployments: number; sourceRetentionDays: number; logRetentionDays: number; orphanRetentionDays: number };
type Job = { id: string; state: string; createdAt: string; error: string | null };
type Preview = { runtimeBytes: number; freeBytes: number; totalBytes: number; expiredLogCount: number; artifacts: { kind: string; name: string; sizeBytes: number; reason: string }[] };
const bytes = (value: number) => `${(value / 1024 / 1024).toFixed(1)} MiB`;
export function Storage({ api }: { api: Api }) {
  const [policy, setPolicy] = useState<Policy>(), [jobs, setJobs] = useState<Job[]>([]), [preview, setPreview] = useState<Preview>(), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  const { confirm } = useUI();
  async function refresh() { try { const data = await api<{ policy: Policy; jobs: Job[] }>('/storage'); setPolicy(current => current ?? data.policy); setJobs(data.jobs); } catch (e) { setError((e as Error).message); } }
  useEffect(() => { void refresh(); const timer = setInterval(() => void refresh(), 5000); return () => clearInterval(timer); }, [api]);
  async function run(task: () => Promise<void>) { setBusy(true); setError(''); try { await task(); } catch (e) { setError((e as Error).message); } finally { setBusy(false); } }
  return <section className="panel form"><h1>Storage cleanup</h1><p>Retain rollback images and remove old source checkouts, logs, and orphaned preview artifacts. Active deployments and queued image references are protected. Database volumes and backups are preserved.</p>{error && <p role="alert">{error}</p>}
    {policy && <form onSubmit={e => { e.preventDefault(); void run(async () => { await api('/storage/policy', policy, 'PUT'); setPreview(undefined); }); }}>
      <label><input type="checkbox" checked={policy.automaticCleanup} onChange={e => setPolicy({ ...policy, automaticCleanup: e.target.checked })} />Automatic daily cleanup</label>
      {([['retainedDeployments', 'Successful deployments retained per project', 100], ['sourceRetentionDays', 'Source retention (days)', 365], ['logRetentionDays', 'Log retention (days)', 365], ['orphanRetentionDays', 'Orphan and deleted-preview grace period (days)', 365]] as const).map(([key, label, max]) => <label key={key}>{label}<input type="number" min={1} max={max} required value={policy[key]} onChange={e => setPolicy({ ...policy, [key]: Number(e.target.value) })} /></label>)}
      <button disabled={busy}>Save retention policy</button>
    </form>}
    <div className="actions"><button className="secondary" disabled={busy} onClick={() => void run(async () => setPreview(await api<Preview>('/storage/preview')))}>Preview cleanup and disk usage</button><button className="danger" disabled={busy || !preview || jobs.some(j => ['Queued', 'Running'].includes(j.state))} onClick={() => void run(async () => { if (await confirm({ title: 'Clean up storage?', message: 'Permanently remove eligible images, source files, and old logs using the saved retention policy. The worker rechecks eligibility before removal. Shared or in-use images may remain.', label: 'Clean up storage', danger: true })) { await api('/storage/cleanup', { confirm: true }); setPreview(undefined); await refresh(); } })}>Run cleanup</button></div>
    {preview && <><p>Runtime files: {bytes(preview.runtimeBytes)} · Free disk: {bytes(preview.freeBytes)} / {bytes(preview.totalBytes)}</p><p>{preview.artifacts.length} eligible artifacts · {preview.expiredLogCount} old log entries. Image sizes can share layers; actual reclaimed space may be lower.</p>{preview.artifacts.map(a => <article key={`${a.kind}:${a.name}`}><strong>{a.kind}</strong> · <code>{a.name}</code> · {bytes(a.sizeBytes)}<p className="field-hint">{a.reason}</p></article>)}</>}
    <h2>Recent cleanup operations</h2>{jobs.map(job => <article key={job.id}>{job.state} · {new Date(job.createdAt).toLocaleString()}{job.error && <p role="alert">{job.error}</p>}</article>)}
  </section>;
}
