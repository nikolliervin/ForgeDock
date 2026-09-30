import { useEffect, useState } from 'react';
type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
export type Database = { id: string; kind: 'PostgreSql' | 'Redis'; state: string; error: string | null; backupIntervalHours?: number; nextBackupAt?: string | null };
export function Databases({ projectId, api }: { projectId: string; api: Api }) {
  const [services, setServices] = useState<Database[]>([]), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  async function refresh() { try { setServices(await api<Database[]>(`/projects/${projectId}/databases`)); } catch (e) { setError((e as Error).message); } }
  useEffect(() => { void refresh(); const timer = setInterval(() => void refresh(), 4000); return () => clearInterval(timer); }, [projectId, api]);
  async function change(kind: string, id?: string) { setBusy(true); setError(''); try { await api(`/projects/${projectId}/databases${id ? `/${id}/retry` : ''}`, id ? {} : { kind }); await refresh(); } catch (e) { setError((e as Error).message); } finally { setBusy(false); } }
  return <section className="panel form"><h2>Database services</h2><p>Add PostgreSQL or Redis on a private project network with persistent storage. Connection variables are added automatically. Deploy your app after the service is running.</p>{error && <p role="alert">{error}</p>}
    <div className="actions">{(['PostgreSql', 'Redis'] as const).map(kind => <button key={kind} disabled={busy || services.some(service => service.kind === kind)} onClick={() => void change(kind)}>Add {kind === 'PostgreSql' ? 'PostgreSQL' : 'Redis'}</button>)}</div>
    {services.map(service => <article key={service.id}><h3>{service.kind === 'PostgreSql' ? 'PostgreSQL' : 'Redis'} · {service.state}</h3><p>Connection variable: <code>{service.kind === 'PostgreSql' ? 'DATABASE_URL' : 'REDIS_URL'}</code></p><p className="field-hint">Private container: <code>forgedock-db-{service.id.replaceAll('-', '')}</code>. No database port is exposed on the host.</p>{service.error && <p role="alert">{service.error}</p>}{['Failed', 'Stopped'].includes(service.state) && <button disabled={busy} onClick={() => void change(service.kind, service.id)}>Start / retry</button>}</article>)}
    <p className="field-hint">Stopping a project also stops its databases. Deleting a project removes its database containers and retains their Docker volumes.</p>
  </section>;
}
