import { useEffect, useRef, useState } from 'react';
import { CopyButton, Icon, useUI } from './ui';
import './storage.css';

type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
type Policy = { automaticCleanup: boolean; retainedDeployments: number; sourceRetentionDays: number; logRetentionDays: number; orphanRetentionDays: number };
type Job = { id: string; state: string; createdAt: string; finishedAt?: string | null; error: string | null; resultJson?: string };
type Artifact = { kind: string; name: string; sizeBytes: number; reason: string };
type Preview = { runtimeBytes: number; freeBytes: number; totalBytes: number; expiredLogCount: number; artifacts: Artifact[] };
type Filter = 'All' | 'Image' | 'Source' | 'Logs';
const fields = [
  ['retainedDeployments', 'Rollback versions', 'Successful deployments retained per project', 'Keep recent successful images for rollback.', 'versions', 100],
  ['sourceRetentionDays', 'Source checkouts', 'Source retention (days)', 'Remove build sources after this period.', 'days', 365],
  ['logRetentionDays', 'Deployment logs', 'Log retention (days)', 'Keep recent deployment and runtime logs.', 'days', 365],
  ['orphanRetentionDays', 'Orphaned artifacts', 'Orphan and deleted-preview grace period (days)', 'Allow a grace period after project deletion.', 'days', 365],
] as const;
function formatBytes(value: number) {
  if (value === 0) return '0 B';
  const unit = Math.min(4, Math.max(0, Math.floor(Math.log(value) / Math.log(1024))));
  return `${(value / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: unit ? 1 : 0 })} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`;
}
function date(value: string) { return new Date(value).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); }
function Glyph({ kind }: { kind: 'disk' | 'files' | 'image' | 'logs' | 'clock' | 'shield' }) {
  const paths = { disk: 'M4 4h16v16H4z M4 14h16 M7 17h.01 M10 17h.01', files: 'M8 3h8l4 4v14H8z M16 3v5h4 M4 7v14', image: 'M12 3l9 5v9l-9 5-9-5V8z M3 8l9 5 9-5 M12 13v9', logs: 'M5 4h14v16H5z M8 8h8 M8 12h8 M8 16h5', clock: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 2', shield: 'M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M8 12l3 3 5-6' };
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[kind]} /></svg>;
}
function removedCount(job: Job) { try { return job.resultJson ? (JSON.parse(job.resultJson) as unknown[]).length : null; } catch { return null; } }

export function Storage({ api }: { api: Api }) {
  const [policy, setPolicy] = useState<Policy>(), [savedPolicy, setSavedPolicy] = useState<Policy>(), [jobs, setJobs] = useState<Job[]>([]);
  const [preview, setPreview] = useState<Preview>(), [updatedAt, setUpdatedAt] = useState<Date>(), [filter, setFilter] = useState<Filter>('All'), [search, setSearch] = useState('');
  const [error, setError] = useState(''), [busy, setBusy] = useState(false), [scanning, setScanning] = useState(true);
  const mounted = useRef(false), jobStates = useRef(new Map<string, string>());
  const { confirm } = useUI();
  async function refreshPreview() {
    setScanning(true);
    try { const result = await api<Preview>('/storage/preview'); if (mounted.current) { setPreview(result); setUpdatedAt(new Date()); } }
    finally { if (mounted.current) setScanning(false); }
  }
  async function refresh() {
    const data = await api<{ policy: Policy; jobs: Job[] }>('/storage');
    if (!mounted.current) return;
    setPolicy(current => current ?? data.policy); setSavedPolicy(current => current ?? data.policy); setJobs(data.jobs);
    const completed = data.jobs.some(job => ['Queued', 'Running'].includes(jobStates.current.get(job.id) ?? '') && !['Queued', 'Running'].includes(job.state));
    jobStates.current = new Map(data.jobs.map(job => [job.id, job.state]));
    if (completed) await refreshPreview();
  }
  useEffect(() => {
    mounted.current = true;
    const onError = (e: Error) => { if (mounted.current) setError(e.message); };
    void refresh().catch(onError); void refreshPreview().catch(onError);
    const timer = setInterval(() => void refresh().catch(onError), 5000);
    return () => { mounted.current = false; clearInterval(timer); };
  }, [api]);
  async function run(task: () => Promise<void>) { setBusy(true); setError(''); try { await task(); } catch (e) { setError((e as Error).message); } finally { setBusy(false); } }
  const dirty = !!policy && !!savedPolicy && JSON.stringify(policy) !== JSON.stringify(savedPolicy);
  const pending = jobs.some(job => ['Queued', 'Running'].includes(job.state));
  const usedPercent = preview?.totalBytes ? Math.max(0, Math.min(100, (preview.totalBytes - preview.freeBytes) / preview.totalBytes * 100)) : 0;
  const artifactBytes = preview?.artifacts.reduce((sum, artifact) => sum + artifact.sizeBytes, 0) ?? 0;
  const counts = { All: (preview?.artifacts.length ?? 0) + (preview?.expiredLogCount ? 1 : 0), Image: preview?.artifacts.filter(a => a.kind === 'Image').length ?? 0, Source: preview?.artifacts.filter(a => a.kind === 'Source').length ?? 0, Logs: preview?.expiredLogCount ? 1 : 0 };
  const rows = [...(preview?.artifacts ?? []), ...(preview?.expiredLogCount ? [{ kind: 'Logs', name: 'Deployment logs', sizeBytes: -1, reason: `${preview.expiredLogCount.toLocaleString()} entries older than ${savedPolicy?.logRetentionDays ?? 30} days` }] : [])]
    .filter(artifact => (filter === 'All' || artifact.kind === filter) && `${artifact.name} ${artifact.reason}`.toLowerCase().includes(search.toLowerCase()));
  const eligible = !!preview && (preview.artifacts.length > 0 || preview.expiredLogCount > 0);

  return <div className="storage-page">
    <div className="storage-heading"><div><span className="caption">WORKSPACE</span><h1>Storage</h1><p>Manage disk usage and keep deployment history under control.</p></div><a className="storage-guide" href="/docs/storage-cleanup">Cleanup guide <Icon name="external" size={13} /></a></div>
    {error && <div className="storage-error" role="alert"><span>!</span><p>{error}</p><button className="icon-button secondary" aria-label="Dismiss storage error" onClick={() => setError('')}>×</button></div>}
    <section className="storage-stats" aria-label="Storage overview" aria-busy={scanning}>
      <div className="panel storage-stat"><div className="storage-stat-label">Disk usage<Glyph kind="disk" /></div><strong>{preview ? `${usedPercent.toFixed(0)}%` : '—'}</strong><div className={'storage-meter' + (usedPercent >= 90 ? ' storage-meter-warning' : '')} role="progressbar" aria-label="Disk used" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(usedPercent)}><span style={{ width: `${usedPercent}%` }} /></div><p>{preview ? `${formatBytes(preview.freeBytes)} free of ${formatBytes(preview.totalBytes)}` : 'Loading disk capacity…'}</p></div>
      <div className="panel storage-stat"><div className="storage-stat-label">Runtime files<Glyph kind="files" /></div><strong>{preview ? formatBytes(preview.runtimeBytes) : '—'}</strong><p>Deployment and platform files</p></div>
      <div className="panel storage-stat"><div className="storage-stat-label">Eligible artifacts<Glyph kind="image" /></div><strong>{preview ? preview.artifacts.length.toLocaleString() : '—'}</strong><p>{preview ? `${formatBytes(artifactBytes)} in images and sources` : 'Scanning retained artifacts…'}</p></div>
      <div className="panel storage-stat"><div className="storage-stat-label">Old log entries<Glyph kind="logs" /></div><strong>{preview ? preview.expiredLogCount.toLocaleString() : '—'}</strong><p>Beyond the saved retention period</p></div>
    </section>
    <div className="storage-layout">
      <section className="panel storage-policy"><div className="storage-section-heading"><h2>Retention policy</h2>{dirty && <span className="storage-unsaved">Unsaved</span>}</div><p className="storage-description">Choose what to keep. Changes apply to the next cleanup.</p>
        {policy ? <form onSubmit={e => { e.preventDefault(); void run(async () => { await api('/storage/policy', policy, 'PUT'); setSavedPolicy({ ...policy }); await refreshPreview(); }); }}>
          <div className="storage-policy-fields">{fields.map(([key, title, accessibleName, hint, unit, max]) => <div className="storage-policy-field" key={key}><label htmlFor={`storage-${key}`}>{title}</label><p>{hint}</p><div className="storage-number"><input id={`storage-${key}`} aria-label={accessibleName} type="number" min={1} max={max} required disabled={busy} value={policy[key]} onChange={e => setPolicy({ ...policy, [key]: Number(e.target.value) })} /><span>{unit}</span></div></div>)}</div>
          <label className="storage-automation"><span><strong>Automatic cleanup</strong><small>Run once a day using this policy.</small></span><input type="checkbox" aria-label="Automatic daily cleanup" disabled={busy} checked={policy.automaticCleanup} onChange={e => setPolicy({ ...policy, automaticCleanup: e.target.checked })} /></label>
          <div className="storage-policy-footer"><button disabled={busy || !dirty}>Save retention policy</button>{dirty && <button type="button" className="secondary" disabled={busy} onClick={() => setPolicy(savedPolicy)}>Discard</button>}</div>
        </form> : <div className="storage-empty">Loading retention settings…</div>}
        <div className="storage-protected"><Glyph kind="shield" /><div><strong>Your active releases are protected</strong><p>Cleanup preserves active images, queued rollback references, backups, and persistent volumes.</p></div></div>
      </section>
      <div className="storage-content">
        <section className="panel storage-preview"><div className="storage-section-heading"><div><h2>Cleanup preview</h2><p className="storage-description">Review eligible artifacts before removing them.</p></div><button className="secondary storage-refresh" aria-label="Preview cleanup and disk usage" disabled={busy || scanning} onClick={() => void run(refreshPreview)}><Icon name="restart" size={14} /><span>{scanning ? 'Scanning…' : 'Refresh preview'}</span></button></div>
          <div className="storage-preview-tools"><div className="storage-filters" role="tablist" aria-label="Artifact types">{(['All', 'Image', 'Source', 'Logs'] as const).map(kind => <button key={kind} role="tab" aria-selected={filter === kind} onClick={() => setFilter(kind)}>{kind === 'Image' ? 'Images' : kind === 'Source' ? 'Sources' : kind}<span>{counts[kind]}</span></button>)}</div><div className="storage-search"><Icon name="search" size={14} /><input aria-label="Search cleanup artifacts" type="search" placeholder="Search artifacts…" value={search} onChange={e => setSearch(e.target.value)} /></div></div>
          {preview && rows.length > 0 ? <div className="storage-table-scroll"><table className="storage-table"><thead><tr><th scope="col">Artifact</th><th scope="col" className="storage-type-column">Type</th><th scope="col" className="storage-size-column">Size</th></tr></thead><tbody>{rows.map(artifact => <tr key={`${artifact.kind}:${artifact.name}`}><td><div className="storage-artifact"><span className="storage-artifact-icon"><Glyph kind={artifact.kind === 'Image' ? 'image' : artifact.kind === 'Logs' ? 'logs' : 'files'} /></span><div className="storage-artifact-text"><div><code title={artifact.name}>{artifact.name}</code>{artifact.kind !== 'Logs' && <CopyButton value={artifact.name} label="artifact name" />}</div><p>{artifact.reason}</p></div></div></td><td className="storage-type-column"><span className="storage-kind">{artifact.kind === 'Image' ? 'Image' : artifact.kind === 'Source' ? 'Source' : 'Logs'}</span></td><td className="storage-size-column">{artifact.sizeBytes < 0 ? '—' : formatBytes(artifact.sizeBytes)}</td></tr>)}</tbody></table></div> : <div className="storage-empty"><span className="storage-empty-icon"><Glyph kind={scanning ? 'disk' : 'shield'} /></span><strong>{scanning && !preview ? 'Scanning storage' : search || filter !== 'All' ? 'No matching artifacts' : preview ? 'Storage is up to date' : 'Preview unavailable'}</strong><p>{scanning && !preview ? 'Checking disk usage and saved retention rules.' : search || filter !== 'All' ? 'Try another filter or search term.' : preview ? 'There are no artifacts eligible for this cleanup.' : 'Refresh the preview to check eligible artifacts.'}</p></div>}
          <div className="storage-preview-footer"><div><p>{dirty ? 'Save your policy changes before running cleanup.' : pending ? 'A cleanup is queued or running.' : 'Removal is permanent. You’ll confirm before it starts.'}</p><small>{updatedAt ? `Updated ${updatedAt.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })} · ` : ''}Shared image layers can reduce reclaimed space.</small></div><button className="danger" disabled={busy || scanning || dirty || pending || !eligible} onClick={() => void run(async () => { if (await confirm({ title: 'Clean up storage?', message: 'Permanently remove eligible images, source files, and old logs using the saved retention policy. The worker rechecks eligibility before removal. Shared or in-use images may remain.', label: 'Clean up storage', danger: true })) { await api('/storage/cleanup', { confirm: true }); await refresh(); } })}>Run cleanup</button></div>
        </section>
        <section className="panel storage-history"><div className="storage-section-heading"><div><h2>Cleanup history</h2><p className="storage-description">Recent manual and automatic operations.</p></div><span className="storage-history-count">{jobs.length} runs</span></div>{jobs.length ? <div className="storage-table-scroll"><table className="storage-table"><thead><tr><th scope="col">Status</th><th scope="col">Started</th><th scope="col" className="storage-size-column">Removed</th></tr></thead><tbody>{jobs.map(job => <tr key={job.id}><td><span className={`storage-job-state storage-job-${job.state.toLowerCase()}`}><span />{job.state}</span>{job.error && <details className="storage-job-error"><summary>View error</summary><p>{job.error}</p></details>}</td><td><time dateTime={job.createdAt} title={new Date(job.createdAt).toLocaleString()}>{date(job.createdAt)}</time></td><td className="storage-size-column">{job.state === 'Completed' && removedCount(job) !== null ? `${removedCount(job)} artifacts` : '—'}</td></tr>)}</tbody></table></div> : <div className="storage-empty storage-history-empty"><span className="storage-empty-icon"><Glyph kind="clock" /></span><strong>No cleanup runs yet</strong><p>Manual and automatic cleanups will appear here.</p></div>}</section>
      </div>
    </div>
  </div>;
}
