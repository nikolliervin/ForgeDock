import { useEffect, useState } from 'react';
import { navigate } from './navigation';
import { CopyButton, Skeleton, useUI } from './ui';

type Domain = { id: string; hostname: string; state: 'PendingDns' | 'AwaitingDeployment' | 'Provisioning' | 'Active' | 'Error' | 'Removing'; verificationRequested: boolean; dnsVerifiedAt: string | null; certificateExpiresAt: string | null; certificateTrusted: boolean; error: string | null; dnsRecords: { type: string; name: string; value: string }[] };
type Hosting = { enabled: boolean; ready: boolean; setupMessage: string | null; target: string; addresses: string[]; stagingCertificates: boolean };
type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
const states = { PendingDns: 'Awaiting DNS', AwaitingDeployment: 'Ready for deployment', Provisioning: 'Issuing certificate', Active: 'HTTPS active', Error: 'Needs attention', Removing: 'Removing' };
const descriptions = { PendingDns: 'Add the DNS records below. We check them automatically; you can also request a check.', AwaitingDeployment: 'DNS is verified. Deploy your application to make it available at this domain.', Provisioning: 'DNS is verified. Your HTTPS certificate is being prepared. Ports 80 and 443 must reach this server.', Active: 'Your application is available over HTTPS. Certificates renew automatically.', Error: 'Review the message below and retry after correcting the issue.', Removing: 'This domain is being detached. Your project and local URL are kept.' };

function RecordValue({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const [failed, setFailed] = useState(false);
  return <div className="domain-record-value"><code>{value}</code><button type="button" className="secondary" aria-label={'Copy ' + value} onClick={() => {
    void (async () => { try { await navigator.clipboard.writeText(value); setCopied(true); setFailed(false); } catch { setFailed(true); } })();
  }}>{copied ? 'Copied ✓' : failed ? 'Select to copy' : 'Copy'}</button></div>;
}

export function Domains({ projectId, api, action, busy }: { projectId: string; api: Api; action: (task: () => Promise<void>) => Promise<void>; busy: boolean }) {
  const { confirm } = useUI();
  const [domains, setDomains] = useState<Domain[]>([]);
  const [hosting, setHosting] = useState<Hosting | null>(null);
  const [hostname, setHostname] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  async function refresh() { setDomains(await api<Domain[]>(`/projects/${projectId}/domains`)); }
  useEffect(() => {
    let disposed = false;
    setDomains([]); setLoading(true); setError('');
    async function load() {
      try {
        const [list, config] = await Promise.all([api<Domain[]>(`/projects/${projectId}/domains`), api<Hosting>('/hosting')]);
        if (!disposed) { setDomains(list); setHosting(config); setError(''); }
      } catch (problem) { if (!disposed) setError((problem as Error).message); }
      finally { if (!disposed) setLoading(false); }
    }
    void load(); const interval = setInterval(load, 4000);
    return () => { disposed = true; clearInterval(interval); };
  }, [projectId]);
  return <section className="domains-section"><div className="title-row"><div><h2>Custom domains</h2><p>Give your application a public address with automatic HTTPS.</p></div><a className="domain-help" href="/docs/custom-domains" onClick={event => navigate(event, '/docs/custom-domains')}>Setup guide ↗</a></div>
    {error && <p role="alert" className="error">{error}</p>}
    {hosting && !hosting.ready && <div className="panel domain-setup"><span className="badge">Server setup required</span><h3>Prepare your server for public domains</h3><p>Configure the server's public DNS target, IP addresses, and certificate registration email, then enable the HTTPS edge. Your local application URL works as before.</p><a href="/docs/custom-domains" onClick={event => navigate(event, '/docs/custom-domains')}>Read the setup guide →</a></div>}
    {hosting?.stagingCertificates && <p className="domain-notice">This server uses a test certificate authority. Certificates issued here may not be trusted by browsers.</p>}
    <form className="panel domain-add" onSubmit={event => { event.preventDefault(); void action(async () => { await api(`/projects/${projectId}/domains`, { hostname }); setHostname(''); await refresh(); }); }}>
      <label>Domain name<input name="hostname" type="text" autoComplete="off" placeholder="app.example.com" maxLength={253} required value={hostname} onChange={event => setHostname(event.target.value)} disabled={!hosting?.ready || busy} /></label><button disabled={!hosting?.ready || busy}>Add domain</button>
    </form>
    {loading ? <Skeleton label="Loading domains…" rows={3} /> : !domains.length ? <div className="panel domain-empty"><span className="domain-empty-icon" aria-hidden="true">◎</span><h3>No custom domains yet</h3><span className="sr-only">Your application's next address</span><p>Add a domain you own. We'll verify its DNS, secure it with a certificate, and keep HTTPS up to date.</p><button className="secondary" disabled={!hosting?.ready || busy} onClick={() => document.querySelector<HTMLInputElement>('input[name=hostname]')?.focus()}>Add your first domain</button></div> : <div className="domain-list">{domains.map(domain => <article className="panel domain-card" key={domain.id}><div className="title-row"><div><a className="domain-hostname" href={'https://' + domain.hostname} target="_blank" rel="noreferrer">{domain.hostname} ↗</a><CopyButton value={domain.hostname} label="domain" /><small>{domain.certificateExpiresAt ? `Certificate expires ${new Date(domain.certificateExpiresAt).toLocaleDateString()}` : 'A certificate will be issued after DNS verification.'}</small></div><span className={'domain-state state-' + domain.state.toLowerCase()}>{states[domain.state]}{domain.state === 'Active' && !domain.certificateTrusted ? ' · test certificate' : ''}</span></div>
      <p>{descriptions[domain.state]}</p>{domain.error && <p className="domain-notice" role="status">{domain.error}</p>}
      {domain.state !== 'Removing' && <details open={!domain.dnsVerifiedAt}><summary>DNS configuration <span>{domain.dnsVerifiedAt ? 'Verified ✓' : 'Required'}</span></summary><p>At your DNS provider, add the following records. Some providers expect relative record names; use <code>@</code> for your root domain.</p><div className="domain-records"><table><thead><tr><th>Type</th><th>Name</th><th>Value</th></tr></thead><tbody>{domain.dnsRecords.map(record => <tr key={record.type + record.value}><td><span className="badge">{record.type}</span></td><td><RecordValue value={record.name} /></td><td><RecordValue value={record.value} /></td></tr>)}</tbody></table></div>{hosting?.target && <p>For a subdomain, you can use a CNAME to <code>{hosting.target}</code> instead of the A/AAAA records. Keep the TXT record for ownership verification. Disable DNS proxying while verifying.</p>}</details>}
      <div className="domain-actions"><button className="secondary" disabled={busy || domain.state === 'Removing' || domain.verificationRequested || !hosting?.ready} onClick={() => void action(async () => { await api(`/projects/${projectId}/domains/${domain.id}/verify`, {}); await refresh(); })}>{domain.verificationRequested ? 'Verification queued…' : 'Verify DNS'}</button><button className="secondary domain-remove" disabled={busy || domain.state === 'Removing'} onClick={() => { void (async () => { if (await confirm({ title: `Remove ${domain.hostname}?`, message: 'Detach this domain and its HTTPS route from the project. Your application and local URL remain available.', label: 'Remove domain', danger: true })) await action(async () => { await api(`/projects/${projectId}/domains/${domain.id}`, undefined, 'DELETE'); await refresh(); }); })(); }}>Remove domain</button></div>
    </article>)}</div>}
  </section>;
}
