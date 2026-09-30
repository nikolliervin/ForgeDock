import React, { useCallback, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';
import './polish.css';
import { Docs } from './Docs';
import { Domains } from './Domains';
import { Metrics } from './Metrics';
import { lazy, Suspense } from 'react';
const Console = lazy(() => import('./Console').then(module => ({ default: module.Console })));
import { WebhookSettings } from './WebhookSettings';
import { EnvironmentImport } from './EnvironmentImport';
import { navigate, goTo } from './navigation';
import { CopyButton, Icon, Menu, Modal, Skeleton, ThemeToggle, UIProvider, useUI } from './ui';
import { DeploymentView, inProgress, type Deployment, type Log } from './Deployments';

type Project = { id: string; name: string; repositoryUrl: string; branch: string; dockerfile: string; containerPort: number; healthPath: string; activeDeploymentId: string | null; healthStatus: string; deploymentMode: 'Dockerfile' | 'Compose' | 'Auto'; buildCommand: string; startCommand: string; rootDirectory: string; composeFile: string; composeService: string };
type Tab = 'deployments' | 'metrics' | 'domains' | 'console' | 'environment' | 'settings';
const tabs: Tab[] = ['deployments', 'metrics', 'domains', 'console', 'environment', 'settings'];

function DeploymentFields({ project }: { project?: Project }) {
  const [mode, setMode] = useState(project?.deploymentMode ?? 'Auto');
  return <><label>Deployment type<select name="deploymentMode" value={mode} onChange={event => setMode(event.target.value as typeof mode)}>
    <option value="Auto">Auto — detect and build your application</option><option value="Dockerfile">Dockerfile — single application</option><option value="Compose">Docker Compose — multiple services</option>
  </select></label>{mode !== 'Compose' ? <><label>Root directory <span className="field-hint">Relative to the repository</span><input name="rootDirectory" required defaultValue={project?.rootDirectory ?? '.'} placeholder="backend" /></label><label>Dockerfile path <span className="field-hint">Relative to the root directory</span><input name="dockerfile" required defaultValue={project?.dockerfile ?? 'Dockerfile'} /></label></>
    : <><label>Compose file path<input name="composeFile" required defaultValue={project?.composeFile ?? 'docker-compose.yml'} /></label>
      <label>Public service<input name="composeService" required defaultValue={project?.composeService ?? ''} placeholder="frontend" /></label>
      <p>The public service receives application traffic. Use its internal port below. Other services stay on the stack's private networks. Named data volumes persist across deployments; updates may briefly interrupt service.</p></>}{mode === 'Auto' && <><p>Uses the Dockerfile above when present. Otherwise Railpack detects your runtime and builds the application. Overrides below apply to Railpack builds.</p><label>Build command <span className="field-hint">Optional · detected automatically</span><input name="buildCommand" maxLength={4096} defaultValue={project?.buildCommand ?? ''} placeholder="npm run build" /></label><label>Start command <span className="field-hint">Optional · detected automatically</span><input name="startCommand" maxLength={4096} defaultValue={project?.startCommand ?? ''} placeholder="npm start" /></label></>}</>;
}

function App({ pathname }: { pathname: string }) {
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
  const [tab, setTab] = useState<'deployments' | 'metrics' | 'domains' | 'console' | 'environment' | 'settings'>('deployments');
  const { notify, confirm } = useUI();
  const [search, setSearch] = useState('');
  const [collapsed, setCollapsed] = useState(false);
  const [commitDialog, setCommitDialog] = useState(false);
  const [commitInput, setCommitInput] = useState('');
  const [projectsLoading, setProjectsLoading] = useState(true);
  const [environmentLoading, setEnvironmentLoading] = useState(false);
  const searchInput = useRef<HTMLInputElement>(null);
  const observed = useRef(new Map<string, string>());
  function openProject(id: string | null, section: Tab = 'deployments') { setSelected(id); setCreating(false); setTab(section); goTo(id ? `/projects/${id}/${section}` : '/'); }
  function changeTab(section: Tab) { if (selected) openProject(selected, section); }
  useEffect(() => {
    if (pathname.startsWith('/docs')) return;
    const match = pathname.match(/^\/projects\/([^/]+)(?:\/([^/]+))?$/);
    setSelected(match?.[1] ?? null);
    setTab(tabs.includes(match?.[2] as Tab) ? match![2] as Tab : 'deployments');
    setCreating(false);
  }, [pathname]);
  useEffect(() => { const key = (e: KeyboardEvent) => { if (e.key === '/' && !['INPUT', 'TEXTAREA', 'SELECT'].includes((e.target as HTMLElement).tagName) && !document.querySelector('dialog[open]') && !pathname.startsWith('/docs')) { e.preventDefault(); setCollapsed(false); searchInput.current?.focus(); } }; window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key); }, [pathname]);
  const project = projects.find(p => p.id === selected);
  const deployment = deployments.find(d => d.id === deploymentId) ?? deployments[0];
  const deploying = deployments.some(d => inProgress(d.state));
  const latest = deployments[0];
  useEffect(() => { if (!pathname.startsWith('/docs')) document.title = project ? `${project.name} · ${tab[0].toUpperCase() + tab.slice(1)} · ForgeDock` : 'Projects · ForgeDock'; }, [project?.name, tab, pathname]);
  const api = useCallback(async <T,>(path: string, body?: unknown, method?: string): Promise<T> => {
    const response = await fetch('/api' + path, { method: method ?? (body === undefined ? 'GET' : 'POST'),
      headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      if (response.status === 401) setAuthenticated(false);
      throw new Error(problem.errors ? Object.values(problem.errors).flat().join(' ') : problem.error ?? problem.detail ?? problem.title ?? `Request failed (${response.status})`);
    }
    const verb = method ?? (body === undefined ? 'GET' : 'POST');
    if (verb !== 'GET' && !path.endsWith('/session') && !path.endsWith('/console')) {
      const message = path.includes('/webhook') ? 'Auto-deploy settings saved' : path.includes('/environment') ? verb === 'DELETE' ? 'Environment variable removed' : 'Environment variables saved' : path.includes('/domains') ? verb === 'DELETE' ? 'Domain removal requested' : path.endsWith('/verify') ? 'DNS verification requested' : 'Domain added' : path.endsWith('/cancel') ? 'Deployment cancelled' : path.includes('/deployments') || path.endsWith('/rollback') || path.endsWith('/redeploy') || path.endsWith('/restart') ? verb === 'DELETE' ? 'Deployment history deleted' : 'Deployment started' : verb === 'PUT' ? 'Settings saved' : path.endsWith('/operations') ? 'Project operation requested' : 'Project created';
      window.dispatchEvent(new CustomEvent('forgedock-notice', { detail: message }));
    }
    return response.status === 204 ? undefined as T : response.json();
  }, [token]);
  useEffect(() => { const notice = (event: Event) => notify((event as CustomEvent<string>).detail); window.addEventListener('forgedock-notice', notice); return () => window.removeEventListener('forgedock-notice', notice); }, [notify]);
  async function action(task: () => Promise<void>) {
    setBusy(true); setError('');
    try { await task(); } catch (e) { const message = e instanceof Error ? e.message : 'Request failed.'; setError(message); notify(message, 'error'); }
    finally { setBusy(false); }
  }
  async function operate(kind: 'Stop' | 'Delete') {
    if (!project) return;
    const operation = await api<{ id: string }>(`/projects/${project.id}/operations`, { kind });
    for (let attempt = 0; attempt < 60; attempt++) {
      const state = await api<{ state: string; error: string | null }>(`/operations/${operation.id}`);
      if (state.state === 'Failed') throw new Error(state.error ?? 'Project operation failed.');
      if (state.state === 'Completed') { if (kind === 'Delete') openProject(null); await refreshProjects(); return; }
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
        if (!disposed) { setProjects(list); setProjectsLoading(false); }
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
        if (!disposed) {
          for (const item of list) { const prior = observed.current.get(item.id); if (prior && inProgress(prior) && ['Failed', 'Running', 'Cancelled'].includes(item.state)) notify(`Deployment ${item.state.toLowerCase()}`, item.state === 'Failed' ? 'error' : 'success'); observed.current.set(item.id, item.state); }
          setDeployments(list);
        }
      } catch (e) { if (!disposed) setError((e as Error).message); }
      finally { if (!disposed) setLoading(false); }
    }
    void refresh(); const interval = setInterval(refresh, 2000);
    return () => { disposed = true; clearInterval(interval); };
  }, [selected, authenticated]);
  useEffect(() => {
    setEnvironment([]); setEnvironmentLoading(true);
    if (!selected || !authenticated) return;
    let disposed = false;
    void api<string[]>(`/projects/${selected}/environment`).then(values => { if (!disposed) { setEnvironment(values); setEnvironmentLoading(false); } }).catch(e => { if (!disposed) { setError(e.message); setEnvironmentLoading(false); } });
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
    function acceptDeployment(d: Deployment) { observed.current.set(d.id, d.state); setDeployments(old => [d, ...old]); setDeploymentId(d.id); changeTab('deployments'); }
  function deploy(commitSha?: string) { if (!project) return; void action(async () => { const d = await api<Deployment>(`/projects/${project.id}/deployments`, commitSha ? { commitSha } : {}); acceptDeployment(d); setCommitDialog(false); setCommitInput(''); }); }
  function redeploy(d: Deployment) { void action(async () => acceptDeployment(await api<Deployment>(`/deployments/${d.id}/redeploy`, {}))); }
  function rollback(d: Deployment) { void (async () => { if (await confirm({ title: 'Roll back deployment?', message: 'Restore this retained image and its original environment variables. Traffic switches after health checks pass.', label: 'Roll back' })) await action(async () => acceptDeployment(await api<Deployment>(`/deployments/${d.id}/rollback`, {}))); })(); }
  function cancelDeployment(d: Deployment) { void (async () => { if (await confirm({ title: 'Cancel queued deployment?', message: 'This revision will be removed from the deployment queue. The current application keeps serving traffic.', label: 'Cancel deployment', danger: true })) await action(async () => { await api(`/deployments/${d.id}/cancel`, {}, 'POST'); setDeployments(await api<Deployment[]>(`/projects/${d.projectId}/deployments`)); }); })(); }
  function deleteDeployment(d: Deployment) { void (async () => { if (await confirm({ title: 'Delete deployment history?', message: 'Remove this failed or cancelled deployment and its logs. This does not remove retained source files, images, or containers.', label: 'Delete entry', danger: true })) await action(async () => { await api(`/deployments/${d.id}`, undefined, 'DELETE'); setDeployments(await api<Deployment[]>(`/projects/${d.projectId}/deployments`)); setDeploymentId(null); }); })(); }
  function deleteProject() { if (!project) return; void (async () => { if (await confirm({ title: `Delete ${project.name}?`, message: 'Stop and remove project containers, routes, variables, and deployment history. Compose data volumes are preserved.', label: 'Delete project', danger: true })) await action(() => operate('Delete')); })(); }
  const projectStatus = latest?.state === 'Failed' ? 'Deployment failed' : latest?.state === 'Cancelled' ? 'Deployment cancelled' : latest && inProgress(latest.state) ? `Deployment ${latest.state.toLowerCase()}` : project?.healthStatus === 'NotDeployed' ? 'Not deployed' : project?.healthStatus ?? 'Not deployed';
  if (!authenticated) return <main className="login"><div className="brand">◈ ForgeDock</div><a className="login-docs" href="/docs" onClick={event => navigate(event, "/docs")}>Documentation ↗</a><h1>Your deployment control room.</h1><p>Sign in with your management token. It stays in memory for this session.</p>
    <form onSubmit={e => { e.preventDefault(); void action(async () => { await api('/session'); setAuthenticated(true); }); }}>
      <label>Management token<input type="password" autoComplete="off" required value={token} onChange={e => setToken(e.target.value)} /></label>
      <button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
    </form>{error && <p role="alert" className="error">{error}</p>}</main>;
  return <div className={'layout ' + (collapsed ? 'sidebar-collapsed' : '')}><aside className="sidebar"><div className="sidebar-brand"><div className="brand"><Icon name="project" size={24} /><span>ForgeDock</span></div><button className="icon-button secondary collapse-toggle" aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} onClick={() => setCollapsed(!collapsed)}><Icon name={collapsed ? 'expand' : 'collapse'} /></button></div><span className="caption">WORKSPACE</span><nav className="workspace-nav" aria-label="Main navigation"><button className={'nav ' + (!selected && !creating ? 'active' : '')} title="Projects" onClick={() => openProject(null)}><Icon name="project" /><span>Projects</span></button></nav>
    <div className="sidebar-title"><span>Your projects</span><button aria-label="Create project" title="Create project" onClick={() => { openProject(null); setCreating(true); }}>+</button></div>
    <div className="project-search"><Icon name="search" /><input ref={searchInput} type="search" placeholder="Search projects…" aria-label="Search projects" title="Press / to search projects" value={search} onChange={e => setSearch(e.target.value)} /><kbd>/</kbd></div>
    <nav className="project-nav" aria-label="Projects">{projectsLoading ? <Skeleton label="Loading projects…" /> : projects.filter(p => p.name.toLowerCase().includes(search.toLowerCase())).map(p => <button key={p.id} className={'nav ' + (selected === p.id ? 'active' : '')} aria-label={p.name} title={p.name} onClick={() => openProject(p.id)}><Icon name="git" /><span className="project-name">{p.name}</span><span className={'project-dot ' + p.healthStatus.toLowerCase()} title={p.healthStatus} /></button>)}{!projectsLoading && search && !projects.some(p => p.name.toLowerCase().includes(search.toLowerCase())) && <p className="search-empty">No matching projects</p>}</nav>
    <nav className="resource-nav" aria-label="Resources"><span className="caption">RESOURCES</span><a className="nav" title="Documentation" href="/docs" onClick={event => navigate(event, '/docs')}><span aria-hidden="true">▤</span><span>Documentation</span><Icon name="external" size={12} /></a></nav><div className="sidebar-footer"><ThemeToggle /><div className="version">ForgeDock v0.1.0 · <a href="/docs" onClick={e => navigate(e, '/docs')}>Docs</a> · <a href="https://github.com/nikolliervin/ForgeDock" target="_blank" rel="noreferrer">GitHub ↗</a></div></div>
    <button className="signout" title="Sign out" onClick={() => { setToken(''); setAuthenticated(false); setProjects([]); }}>Sign out</button></aside>
    <main><header><nav className="breadcrumbs" aria-label="Breadcrumb"><a href="/" onClick={event => navigate(event, '/')} >Projects</a>{project && <><span>/</span><a href={`/projects/${project.id}/deployments`} onClick={event => navigate(event, `/projects/${project.id}/deployments`)}>{project.name}</a><span>/</span><span>{tab[0].toUpperCase() + tab.slice(1)}</span></>}</nav><span className="badge environment-badge" title="Applications run on your self-hosted server"><span className="status-dot" />Self-hosted</span></header>
      {error && <div role="alert" className="error">{error}<button onClick={() => setError('')} aria-label="Dismiss error">×</button></div>}
      {creating ? <><h1>Create a project</h1><p>Deploy a public Git repository with automatic builds, a Dockerfile, or a Compose stack.</p><form className="panel form" onSubmit={e => {
        e.preventDefault(); const data = new FormData(e.currentTarget);
        void action(async () => { const p = await api<Project>('/projects', { name: data.get('name'), repositoryUrl: data.get('repository'), branch: data.get('branch'), dockerfile: data.get('dockerfile') ?? 'Dockerfile', deploymentMode: data.get('deploymentMode'), buildCommand: data.get('buildCommand') ?? '', startCommand: data.get('startCommand') ?? '', rootDirectory: data.get('rootDirectory') ?? '.', composeFile: data.get('composeFile') ?? 'docker-compose.yml', composeService: data.get('composeService') ?? '', containerPort: Number(data.get('port')), healthPath: data.get('health') }); await refreshProjects(); openProject(p.id); });
      }}><label>Project name<input name="name" required maxLength={100} placeholder="my-service" /></label><label>Repository URL<input name="repository" type="url" required placeholder="https://github.com/you/service.git" /></label><label>Branch<input name="branch" required defaultValue="main" /></label><DeploymentFields /><div className="columns"><label>Container port<input name="port" type="number" min={1} max={65535} required defaultValue={8080} /></label><label>Health endpoint<input name="health" required defaultValue="/" /></label></div><div className="actions"><button disabled={busy}>Create project</button><button className="secondary" type="button" onClick={() => setCreating(false)}>Cancel</button></div></form></> : project ? <>
        <div className="title-row project-heading"><div className="project-heading-info"><span className="caption">PROJECT</span><h1>{project.name}</h1><div className={'project-status ' + (latest?.state.toLowerCase() ?? project.healthStatus.toLowerCase())}><span className={deploying ? 'spinner' : 'status-dot'} /><span className="project-status-label">{latest ? 'Latest deployment' : 'Application'}</span><span className="metadata-divider">·</span><span className="project-status-state">{latest?.state === 'Failed' ? 'Failed' : latest?.state === 'Cancelled' ? 'Cancelled' : projectStatus}</span>{latest?.state === 'Failed' && project.activeDeploymentId && project.healthStatus === 'Running' && <span className="serving-previous">Serving previous version</span>}</div><div className="repo-metadata"><a href={project.repositoryUrl} target="_blank" rel="noreferrer" title={project.repositoryUrl}><Icon name="git" /><span>{project.repositoryUrl.replace('https://github.com/', '').replace(/\.git$/, '')}</span><Icon name="external" size={12} /></a><CopyButton value={project.repositoryUrl} label="repository URL" /><span title={`Branch: ${project.branch}`}><Icon name="branch" />{project.branch}</span>{latest?.commitSha && <><code title={latest.commitSha}>{latest.commitSha.slice(0, 7)}</code><CopyButton value={latest.commitSha} label="latest commit SHA" /></>}{latest?.commitMessage && <span className="repo-commit-message" title={latest.commitMessage}>{latest.commitMessage}</span>}</div></div><div className="actions project-actions">{project.activeDeploymentId && <><button className="secondary" disabled={busy || deploying} title="Restart the active retained version" onClick={() => void action(async () => acceptDeployment(await api<Deployment>(`/projects/${project.id}/restart`, {})))}>Restart</button><button className="secondary" disabled={busy || deploying || project.healthStatus === 'Stopped'} onClick={() => { void (async () => { if (await confirm({ title: 'Stop application?', message: 'Stop the application and remove its active route.', label: 'Stop application', danger: true })) await action(() => operate('Stop')); })(); }}>Stop</button></>}<div className="deploy-split"><button disabled={busy || deploying} title={deploying ? 'A deployment is already in progress' : 'Deploy the latest revision of the configured branch'} onClick={() => deploy()}>{deploying || busy ? <><span className="spinner" />Deploying…</> : 'Deploy'}</button><Menu label="Deploy options"><button disabled={busy || deploying} onClick={() => deploy()}>Deploy latest</button><button disabled={busy || deploying || !deployment} onClick={() => deployment && redeploy(deployment)}>Redeploy selected revision</button><button disabled={busy || deploying} onClick={() => setCommitDialog(true)}>Deploy specific commit</button></Menu></div><Menu label="Project actions"><button onClick={() => changeTab('settings')}>Project settings</button><button disabled={busy || deploying} className="menu-danger" onClick={deleteProject}>Delete project</button></Menu></div></div>
        <div className="panel route"><span>APPLICATION ROUTE</span><div className="route-address"><a href={`http://${project.id.replaceAll('-', '')}.localhost:8088`} target="_blank" rel="noreferrer">{project.id.replaceAll('-', '')}.localhost:8088 <Icon name="external" size={13} /></a><CopyButton value={`http://${project.id.replaceAll('-', '')}.localhost:8088`} label="application URL" /></div><small>{project.activeDeploymentId ? `Health: ${project.healthStatus}` : 'Available after your first successful deployment.'}</small></div>
        <nav className="tabs" aria-label="Project sections">{(['deployments', 'metrics', 'domains', 'console', 'environment', 'settings'] as const).map(t => <button key={t} className={tab === t ? 'active' : 'secondary'} onClick={() => changeTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>)}</nav>
        {tab === 'console' ? <Suspense fallback={<Skeleton label="Opening terminal…" />}><Console key={`${project.id}:${project.activeDeploymentId}`} projectId={project.id} available={!!project.activeDeploymentId && project.healthStatus !== 'Stopped' && !deploying} api={api} /></Suspense> : tab === 'metrics' ? <Metrics key={project.id} projectId={project.id} api={api} /> : tab === 'domains' ? <Domains key={project.id} projectId={project.id} api={api} action={action} busy={busy} /> : tab === 'environment' ? <section className="panel form"><h2>Environment variables</h2><p>Values are encrypted at rest and never returned by the API. Deploy again to apply changes. Rollback restores the original deployment's variables. Railpack builds also receive saved variables, including RAILPACK_* settings. Frontend build variables can become public in the generated assets. For Compose, reference saved variables using <code>{'${NAME}'}</code> in the Compose file.</p>{environmentLoading ? <Skeleton label="Loading environment variables…" /> : !environment.length ? <div className="empty compact"><h3>No environment variables yet</h3><p>Add application configuration below, or import a .env file.</p><button className="secondary" onClick={() => document.querySelector<HTMLInputElement>('input[name=key]')?.focus()}>Add variable</button></div> : environment.map(name => <div className="variable" key={name}><code>{name}</code><span>••••••••</span><button className="secondary" disabled={busy} onClick={() => { void (async () => { if (await confirm({ title: `Remove ${name}?`, message: 'This applies on the next deployment. Retained versions keep their original variables.', label: 'Remove variable', danger: true })) await action(async () => { await api(`/projects/${project.id}/environment/${name}`, undefined, 'DELETE'); setEnvironment(await api<string[]>(`/projects/${project.id}/environment`)); }); })(); }}>Delete</button></div>)}<form onSubmit={e => { e.preventDefault(); const form = e.currentTarget; const values = new FormData(form); void action(async () => { const name = String(values.get('key')); await api(`/projects/${project.id}/environment/${encodeURIComponent(name)}`, { value: values.get('value') }, 'PUT'); setEnvironment(await api<string[]>(`/projects/${project.id}/environment`)); form.reset(); }); }}><label>Name<input name="key" required pattern="[A-Za-z_][A-Za-z0-9_]*" placeholder="DATABASE_URL" /></label><label>Value<input name="value" type="password" autoComplete="off" required /></label><button disabled={busy}>Save variable</button></form><EnvironmentImport key={project.id} projectId={project.id} api={api} action={action} busy={busy} onSaved={async () => setEnvironment(await api<string[]>(`/projects/${project.id}/environment`))} /></section> : tab === 'settings' ? <section className="panel form"><h2>Project settings</h2><p>Changes apply to future deployments. Work already queued keeps its original configuration.</p><form key={project.id} onSubmit={e => { e.preventDefault(); const values = new FormData(e.currentTarget); void action(async () => { await api(`/projects/${project.id}`, { name: values.get('name'), repositoryUrl: values.get('repository'), branch: values.get('branch'), dockerfile: values.get('dockerfile') ?? 'Dockerfile', deploymentMode: values.get('deploymentMode'), buildCommand: values.get('buildCommand') ?? '', startCommand: values.get('startCommand') ?? '', rootDirectory: values.get('rootDirectory') ?? '.', composeFile: values.get('composeFile') ?? 'docker-compose.yml', composeService: values.get('composeService') ?? '', containerPort: Number(values.get('port')), healthPath: values.get('health') }, 'PUT'); await refreshProjects(); }); }}><label>Name<input name="name" defaultValue={project.name} required /></label><label>Repository URL<input name="repository" type="url" defaultValue={project.repositoryUrl} required /></label><label>Branch<input name="branch" defaultValue={project.branch} required /></label><DeploymentFields project={project} /><label>Container port<input name="port" type="number" min={1} max={65535} defaultValue={project.containerPort} required /></label><label>Health path<input name="health" defaultValue={project.healthPath} required /></label><button disabled={busy}>Save settings</button></form><WebhookSettings key={`webhook:${project.id}`} projectId={project.id} branch={project.branch} api={api} /><hr /><h3>Delete project</h3><p>Stops and removes this project's containers, route, variables, and deployment history. Compose data volumes are preserved. Retained images and source directories require separate cleanup.</p><button className="danger" disabled={busy} onClick={deleteProject}>Delete project</button></section> : <DeploymentView deployments={deployments} deployment={deployment} logs={logs} loading={loading} busy={busy} onSelect={setDeploymentId} onDeploy={() => deploy()} onRedeploy={redeploy} onRollback={rollback} onCancel={cancelDeployment} onDelete={deleteDeployment} />}

      </> : <><div className="title-row"><div><span className="caption">YOUR WORKSPACE</span><h1>Projects</h1><p>Build, deploy, and keep track of your services.</p></div><button onClick={() => setCreating(true)}>New project</button></div>{projects.length ? <div className="project-grid">{projects.map(p => <button key={p.id} className="panel project-card" onClick={() => openProject(p.id)}><h2>{p.name}</h2><p>{p.repositoryUrl}</p><span className="badge">{p.branch}</span><small>{p.healthStatus === 'NotDeployed' ? 'Not deployed' : p.healthStatus}</small></button>)}</div> : <div className="panel empty"><h2>Your first deployment starts here.</h2><p>Connect a repository, choose a build method, and follow the build from source to a running service.</p><button onClick={() => setCreating(true)}>Create your first project</button></div>}</>}
    {commitDialog && <Modal title="Deploy specific commit" onClose={() => setCommitDialog(false)}><p>Enter the full 40-character Git commit SHA from this repository. The deployment uses your current project settings and environment variables.</p><form onSubmit={e => { e.preventDefault(); deploy(commitInput); }}><label>Commit SHA<input autoFocus required pattern="[a-fA-F0-9]{40}" minLength={40} maxLength={40} value={commitInput} onChange={e => setCommitInput(e.target.value)} placeholder="Full commit SHA" /></label><div className="modal-actions"><button type="button" className="secondary" onClick={() => setCommitDialog(false)}>Cancel</button><button disabled={busy}>Deploy commit</button></div></form></Modal>}</main></div>;
}
function Root() {
  const [pathname, setPathname] = useState(window.location.pathname);
  useEffect(() => {
    const update = () => setPathname(window.location.pathname);
    window.addEventListener('popstate', update);
    return () => window.removeEventListener('popstate', update);
  }, []);
  const docs = pathname === '/docs' || pathname.startsWith('/docs/');

  return <><div hidden={docs}><App pathname={pathname} /></div>{docs && <Docs />}</>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><UIProvider><Root /></UIProvider></React.StrictMode>);
