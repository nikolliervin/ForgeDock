import { useState } from 'react';
type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
export function EnvironmentImport({ projectId, api, action, busy, onSaved }: { projectId: string; api: Api; action: (task: () => Promise<void>) => Promise<void>; busy: boolean; onSaved: () => Promise<void> }) {
  const [content, setContent] = useState(''), [overwrite, setOverwrite] = useState(false), [saved, setSaved] = useState<number | null>(null);
  return <details className="environment-import"><summary>Import from .env</summary><p>Paste multiple variables, one per line. Comments, export prefixes, and quoted single-line values are supported. Values are encrypted when saved. Variable references are kept as literal text.</p>
    <form onSubmit={event => { event.preventDefault(); void action(async () => { const result = await api<{ saved: number }>(`/projects/${projectId}/environment`, { content, overwrite }, 'PUT'); setContent(''); setOverwrite(false); setSaved(result.saved); await onSaved(); }); }}>
      <label>Environment file<textarea aria-label="Environment file" value={content} onChange={event => { setContent(event.target.value); setSaved(null); }} spellCheck={false} autoComplete="off" required maxLength={262144} rows={8} placeholder={'NODE_ENV=production\nPORT=8080\nDATABASE_URL="your connection string"'} /></label>
      <label className="environment-overwrite"><input type="checkbox" checked={overwrite} onChange={e => setOverwrite(e.target.checked)} />Replace variables that already exist</label>
      <p className="field-hint">Up to 100 variables per import. Other saved variables are kept. Deploy again to apply changes.</p><button disabled={busy || !content.trim()}>Save variables</button>
    </form>{saved !== null && <p className="environment-saved" role="status">Saved {saved} variable{saved === 1 ? '' : 's'}. Deploy to apply changes.</p>}
  </details>;
}
