import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

type Project = { id: string; name: string; repositoryUrl: string; branch: string; dockerfile: string; containerPort: number; healthPath: string; activeDeploymentId: string | null; healthStatus: string; deploymentMode: 'Dockerfile' | 'Compose'; composeFile: string; composeService: string };
type Deployment = { id: string; projectId: string; state: string; commitSha: string | null; createdAt: string; updatedAt: string; error: string | null; rollbackSourceId: string | null; services: { name: string; state: string; health: string | null; image: string }[] };
type Log = { id: number; timestamp: string; message: string };
const stages = ['Queued', 'Preparing', 'Cloning', 'Building', 'Starting', 'HealthChecking', 'Routing', 'Running'];

function DeploymentFields({ project }: { project?: Project }) {
  const [mode, setMode] = useState(project?.deploymentMode ?? 'Dockerfile');
  return <><label>Deployment type<select name="deploymentMode" value={mode} onChange={event => setMode(event.target.value as typeof mode)}>
    <option value="Dockerfile">Dockerfile — single application</option><option value="Compose">Docker Compose — multiple services</option>
  </select></label>{mode === 'Dockerfile' ? <label>Dockerfile path<input name="dockerfile" required defaultValue={project?.dockerfile ?? 'Dockerfile'} /></label>
    : <><label>Compose file path<input name="composeFile" required defaultValue={project?.composeFile ?? 'docker-compose.yml'} /></label>
      <label>Public service<input name="composeService" required defaultValue={project?.composeService ?? ''} placeholder="frontend" /></label>
      <p>The public service receives application traffic. Use its internal port below. Other services stay on the stack's private networks. Named data volumes persist across deployments; updates may briefly interrupt service.</p></>}</>;
}

function App() {
  const [token, setToken] = useState('');
  const [authenticated, setAuthenticated] = useState(false);
  const [projects, setProjects] = useState<Project[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [deploymentId, setDeploymentId] = useState<string | null>(null);
  const [logs, setLogs] = useState<Log[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [loading, setLoading] = useState(false);
  const [environment, setEnvironment] = useState<string[]>([]);
  const [tab, setTab] = useState<'deployments' | 'environment' | 'settings'>('deployments');
  const project = projects.find(p => p.id === selected);
  const deployment = deployments.find(d => d.id === deploymentId) ?? deployments[0];
  async function api<T>(path: string, body?: unknown, method?: string): Promise<T> {
    const response = await fetch('/api' + path, { method: method ?? (body === undefined ? 'GET' : 'POST'),
      headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      if (response.status === 401) setAuthenticated(false);
      throw new Error(problem.errors ? Object.values(problem.errors).flat().join(' ') : problem.error ?? problem.detail ?? problem.title ?? `Request failed (${response.status})`);
    }
    return response.status === 204 ? undefined as T : response.json();
  }
  async function action(task: () => Promise<void>) {
    setBusy(true); setError('');
    try { await task(); } catch (e) { setError(e instanceof Error ? e.message : 'Request failed.'); }
    finally { setBusy(false); }
  }
  async function operate(kind: 'Stop' | 'Delete') {
    if (!project) return;
    const operation = await api<{ id: string }>(`/projects/${project.id}/operations`, { kind });
    for (let attempt = 0; attempt < 60; attempt++) {
      const state = await api<{ state: string; error: string | null }>(`/operations/${operation.id}`);
      if (state.state === 'Failed') throw new Error(state.error ?? 'Project operation failed.');
      if (state.state === 'Completed') { if (kind === 'Delete') setSelected(null); await refreshProjects(); return; }
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
    throw new Error('Operation is still processing. Refresh to inspect the project state.');
  }
  async function refreshProjects() { setProjects(await api<Project[]>('/projects')); }
  useEffect(() => {
    if (!authenticated) return;
    let disposed = false;
    async function refresh() {
      try {
        const list = await api<Project[]>('/projects');
        if (!disposed) { setProjects(list); }
      } catch (e) { if (!disposed) setError((e as Error).message); }
    }
    void refresh(); const interval = setInterval(refresh, 4000);
    return () => { disposed = true; clearInterval(interval); };
  }, [authenticated]);
  useEffect(() => {
    setDeployments([]); setLogs([]); setDeploymentId(null);
    if (!selected || !authenticated) return;
    let disposed = false; setLoading(true);
    async function refresh() {
      try {
        const list = await api<Deployment[]>(`/projects/${selected}/deployments`);
        if (!disposed) setDeployments(list);
      } catch (e) { if (!disposed) setError((e as Error).message); }
      finally { if (!disposed) setLoading(false); }
    }
    void refresh(); const interval = setInterval(refresh, 2000);
    return () => { disposed = true; clearInterval(interval); };
  }, [selected, authenticated]);
  useEffect(() => {
    setEnvironment([]); setTab('deployments');
    if (!selected || !authenticated) return;
    let disposed = false;
    void api<string[]>(`/projects/${selected}/environment`).then(values => { if (!disposed) setEnvironment(values); }).catch(e => { if (!disposed) setError(e.message); });
    return () => { disposed = true; };
  }, [selected, authenticated]);
  useEffect(() => {
    setLogs([]);
    if (!deployment?.id || !authenticated) return;
    let disposed = false; let after = 0;
    async function refresh() {
      try {
        const lines = await api<Log[]>(`/deployments/${deployment!.id}/logs?after=${after}`);
        if (!disposed && lines.length) { after = lines[lines.length - 1].id; setLogs(old => [...old, ...lines].slice(-3000)); }
      } catch (e) { if (!disposed) setError((e as Error).message); }
    }
    void refresh(); const interval = setInterval(refresh, 1500);
    return () => { disposed = true; clearInterval(interval); };
  }, [deployment?.id, authenticated]);
  if (!authenticated) return <main className="login"><div className="brand">◈ ForgeDock</div><h1>Your deployment control room.</h1><p>Sign in with your management token. It stays in memory for this session.</p>
    <form onSubmit={e => { e.preventDefault(); void action(async () => { await api('/session'); setAuthenticated(true); }); }}>
      <label>Management token<input type="password" autoComplete="off" required value={token} onChange={e => setToken(e.target.value)} /></label>
      <button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
    </form>{error && <p role="alert" className="error">{error}</p>}</main>;
  return <div className="layout"><aside><div className="brand">◈ ForgeDock</div><span className="caption">WORKSPACE</span><button className="nav" onClick={() => { setSelected(null); setCreating(false); }}>Overview</button>
    <div className="sidebar-title">Projects <button aria-label="Create project" onClick={() => setCreating(true)}>+</button></div>
    {projects.map(p => <button key={p.id} className={'nav ' + (selected === p.id ? 'active' : '')} onClick={() => { setSelected(p.id); setCreating(false); }}>{p.name}</button>)}
    <button className="signout" onClick={() => { setToken(''); setAuthenticated(false); setProjects([]); }}>Sign out</button></aside>
    <main><header><span>Workspace / {project?.name ?? 'Overview'}</span><span className="badge">Self-hosted</span></header>
      {error && <div role="alert" className="error">{error}<button onClick={() => setError('')} aria-label="Dismiss error">×</button></div>}
      {creating ? <><h1>Create a project</h1><p>Deploy a public Git repository using a Dockerfile or Compose stack.</p><form className="panel form" onSubmit={e => {
        e.preventDefault(); const data = new FormData(e.currentTarget);
        void action(async () => { const p = await api<Project>('/projects', { name: data.get('name'), repositoryUrl: data.get('repository'), branch: data.get('branch'), dockerfile: data.get('dockerfile') ?? 'Dockerfile', deploymentMode: data.get('deploymentMode'), composeFile: data.get('composeFile') ?? 'docker-compose.yml', composeService: data.get('composeService') ?? '', containerPort: Number(data.get('port')), healthPath: data.get('health') }); await refreshProjects(); setSelected(p.id); setCreating(false); });
      }}><label>Project name<input name="name" required maxLength={100} placeholder="my-service" /></label><label>Repository URL<input name="repository" type="url" required placeholder="https://github.com/you/service.git" /></label><label>Branch<input name="branch" required defaultValue="main" /></label><DeploymentFields /><div className="columns"><label>Container port<input name="port" type="number" min={1} max={65535} required defaultValue={8080} /></label><label>Health endpoint<input name="health" required defaultValue="/" /></label></div><div className="actions"><button disabled={busy}>Create project</button><button className="secondary" type="button" onClick={() => setCreating(false)}>Cancel</button></div></form></> : project ? <>
        <div className="title-row"><div><span className="caption">PROJECT</span><h1>{project.name}</h1><p>{project.repositoryUrl} · {project.branch}</p></div><div className="actions">{project.activeDeploymentId && <><button className="secondary" disabled={busy} onClick={() => void action(async () => { const d = await api<Deployment>(`/projects/${project.id}/restart`, {}); setDeployments(old => [d, ...old]); setDeploymentId(d.id); setTab('deployments'); })}>Restart</button><button className="secondary" disabled={busy || project.healthStatus === 'Stopped'} onClick={() => { if (window.confirm('Stop the application and remove its active route?')) void action(() => operate('Stop')); }}>Stop</button></>}<button disabled={busy} onClick={() => void action(async () => { const d = await api<Deployment>(`/projects/${project.id}/deployments`, {}); setDeployments(old => [d, ...old]); setDeploymentId(d.id); setTab('deployments'); })}>Deploy</button></div></div>
        <div className="panel route"><span>APPLICATION ROUTE</span><a href={`http://${project.id.replaceAll('-', '')}.localhost:8088`} target="_blank" rel="noreferrer">{project.id.replaceAll('-', '')}.localhost:8088 ↗</a><small>{project.activeDeploymentId ? `Health: ${project.healthStatus}` : 'Available after your first successful deployment.'}</small></div>
        <nav className="tabs" aria-label="Project sections">{(['deployments', 'environment', 'settings'] as const).map(t => <button key={t} className={tab === t ? 'active' : 'secondary'} onClick={() => setTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>)}</nav>
        {tab === 'environment' ? <section className="panel form"><h2>Environment variables</h2><p>Values are encrypted at rest and never returned by the API. Deploy again to apply changes. Rollback restores the original deployment's variables. For Compose, reference saved variables using <code>{'${NAME}'}</code> in the Compose file.</p>{environment.map(name => <div className="variable" key={name}><code>{name}</code><span>••••••••</span><button className="secondary" disabled={busy} onClick={() => { if (window.confirm(`Delete ${name}? This applies on the next deployment.`)) void action(async () => { await api(`/projects/${project.id}/environment/${name}`, undefined, 'DELETE'); setEnvironment(await api<string[]>(`/projects/${project.id}/environment`)); }); }}>Delete</button></div>)}<form onSubmit={e => { e.preventDefault(); const form = e.currentTarget; const values = new FormData(form); void action(async () => { const name = String(values.get('key')); await api(`/projects/${project.id}/environment/${encodeURIComponent(name)}`, { value: values.get('value') }, 'PUT'); setEnvironment(await api<string[]>(`/projects/${project.id}/environment`)); form.reset(); }); }}><label>Name<input name="key" required pattern="[A-Za-z_][A-Za-z0-9_]*" placeholder="DATABASE_URL" /></label><label>Value<input name="value" type="password" autoComplete="off" required /></label><button disabled={busy}>Save variable</button></form></section> : tab === 'settings' ? <section className="panel form"><h2>Project settings</h2><p>Changes apply to future deployments. Work already queued keeps its original configuration.</p><form key={project.id} onSubmit={e => { e.preventDefault(); const values = new FormData(e.currentTarget); void action(async () => { await api(`/projects/${project.id}`, { name: values.get('name'), repositoryUrl: values.get('repository'), branch: values.get('branch'), dockerfile: values.get('dockerfile') ?? 'Dockerfile', deploymentMode: values.get('deploymentMode'), composeFile: values.get('composeFile') ?? 'docker-compose.yml', composeService: values.get('composeService') ?? '', containerPort: Number(values.get('port')), healthPath: values.get('health') }, 'PUT'); await refreshProjects(); }); }}><label>Name<input name="name" defaultValue={project.name} required /></label><label>Repository URL<input name="repository" type="url" defaultValue={project.repositoryUrl} required /></label><label>Branch<input name="branch" defaultValue={project.branch} required /></label><DeploymentFields project={project} /><label>Container port<input name="port" type="number" min={1} max={65535} defaultValue={project.containerPort} required /></label><label>Health path<input name="health" defaultValue={project.healthPath} required /></label><button disabled={busy}>Save settings</button></form><hr /><h3>Delete project</h3><p>Stops and removes this project's containers, route, variables, and deployment history. Compose data volumes are preserved. Retained images and source directories require separate cleanup.</p><button className="danger" disabled={busy} onClick={() => { if (window.confirm(`Permanently delete ${project.name} and its deployment history?`)) void action(() => operate('Delete')); }}>Delete project</button></section> : <div className="deployment-grid"><section className="panel"><h2>Deployment history</h2>{loading ? <p>Loading deployments…</p> : !deployments.length ? <p>No deployments yet. Deploy your first version to get started.</p> : deployments.map(d => <button key={d.id} data-deployment-id={d.id} className={'history ' + (deployment?.id === d.id ? 'chosen' : '')} onClick={() => setDeploymentId(d.id)}><span className={'status ' + d.state.toLowerCase()}>{d.state}</span><span>{d.commitSha?.slice(0, 7) ?? 'Awaiting source'}<small>{new Date(d.createdAt).toLocaleString()}</small></span></button>)}</section>
        <section className="panel"><div className="title-row"><h2>Deployment progress</h2>{deployment && ['Running', 'Stopped'].includes(deployment.state) && <button className="secondary" disabled={busy} onClick={() => { if (window.confirm('Deploy this retained version and switch application traffic after it passes health checks?')) void action(async () => { const d = await api<Deployment>(`/deployments/${deployment.id}/rollback`, {}); setDeployments(old => [d, ...old]); setDeploymentId(d.id); }); }}>Roll back</button>}</div>{deployment ? <><ol className="stages">{stages.map((s, i) => <li key={s} className={stages.indexOf(deployment.state) >= i ? 'done' : ''}>{stages.indexOf(deployment.state) > i ? '✓' : deployment.state === s ? '●' : '○'} {s.replace('HealthChecking', 'Health check')}</li>)}</ol>{deployment.error && <p className="error">{deployment.error}</p>}{deployment.services?.length > 0 && <><h3>Stack services</h3><table className="services"><thead><tr><th>Service</th><th>Status</th><th>Health</th></tr></thead><tbody>{deployment.services.map(service => <tr key={service.name}><td>{service.name}</td><td>{service.state}</td><td>{service.health || '—'}</td></tr>)}</tbody></table></>}<h3>Build & deployment logs</h3><pre aria-label="Deployment logs">{logs.length ? logs.map(l => `${new Date(l.timestamp).toLocaleTimeString()}  ${l.message}`).join('\n') : 'Waiting for deployment output…'}</pre></> : <p>Select a deployment to inspect its progress.</p>}</section></div>}
      </> : <><div className="title-row"><div><span className="caption">YOUR WORKSPACE</span><h1>Projects</h1><p>Build, deploy, and keep track of your services.</p></div><button onClick={() => setCreating(true)}>New project</button></div>{projects.length ? <div className="project-grid">{projects.map(p => <button key={p.id} className="panel project-card" onClick={() => setSelected(p.id)}><h2>{p.name}</h2><p>{p.repositoryUrl}</p><span className="badge">{p.branch}</span><small>{p.healthStatus === 'NotDeployed' ? 'Not deployed' : p.healthStatus}</small></button>)}</div> : <div className="panel empty"><h2>Your first deployment starts here.</h2><p>Connect a repository, configure your Dockerfile, and follow the build from source to a running service.</p><button onClick={() => setCreating(true)}>Create your first project</button></div>}</>}
    </main></div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>);
