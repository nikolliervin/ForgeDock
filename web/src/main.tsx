import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';
import './polish.css';
import { Domains } from './Domains';
import { Metrics } from './Metrics';
import { lazy, Suspense } from 'react';
const Docs = lazy(() => import('./Docs').then((module) => ({ default: module.Docs })));
const Console = lazy(() => import('./Console').then((module) => ({ default: module.Console })));
import { NewProject } from './NewProject';
import { ProjectCard } from './ProjectCard';
import './platform.css';
import { Jobs } from './Jobs';
import { Releases } from './Releases';
import { Previews } from './Previews';
import { Storage } from './Storage';
import { Backups } from './Backups';
import { Databases } from './Databases';
import { navigate, goTo } from './navigation';
import { CopyButton, Icon, Menu, Modal, Skeleton, ThemeToggle, UIProvider, useUI } from './ui';
import { DeploymentView, inProgress, type Deployment, type Log } from './Deployments';

import type { Project } from './types';
import { EnvironmentPanel } from './EnvironmentPanel';
import { ProjectSettings } from './ProjectSettings';
import { useManagementApi } from './useManagementApi';
type Tab =
  | 'deployments'
  | 'metrics'
  | 'domains'
  | 'databases'
  | 'backups'
  | 'releases'
  | 'jobs'
  | 'previews'
  | 'console'
  | 'environment'
  | 'settings';
const tabs: Tab[] = [
  'deployments',
  'metrics',
  'domains',
  'databases',
  'backups',
  'releases',
  'jobs',
  'previews',
  'console',
  'environment',
  'settings',
];

function App({ pathname }: { pathname: string }) {
  const [token, setToken] = useState('');
  const [authenticated, setAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<'sso' | 'token' | null>(null);
  const [csrfToken, setCsrfToken] = useState<string>();
  const [authLoading, setAuthLoading] = useState(true);
  const [authProvider, setAuthProvider] = useState('');
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
  const [tab, setTab] = useState<
    | 'deployments'
    | 'metrics'
    | 'domains'
    | 'databases'
    | 'backups'
    | 'releases'
    | 'jobs'
    | 'previews'
    | 'console'
    | 'environment'
    | 'settings'
  >('deployments');
  const { notify, confirm } = useUI();
  const [search, setSearch] = useState('');
  const [collapsed, setCollapsed] = useState(false);
  const [commitDialog, setCommitDialog] = useState(false);
  const [commitInput, setCommitInput] = useState('');
  const [projectsLoading, setProjectsLoading] = useState(true);
  const [environmentLoading, setEnvironmentLoading] = useState(false);
  const searchInput = useRef<HTMLInputElement>(null);
  const observed = useRef(new Map<string, string>());
  function openProject(id: string | null, section: Tab = 'deployments') {
    setSelected(id);
    setCreating(false);
    setTab(section);
    goTo(id ? `/projects/${id}/${section}` : '/dashboard');
  }
  function changeTab(section: Tab) {
    if (selected) openProject(selected, section);
  }
  useEffect(() => {
    if (pathname.startsWith('/docs')) return;
    const match = pathname.match(/^\/projects\/([^/]+)(?:\/([^/]+))?$/);
    setSelected(match?.[1] ?? null);
    setTab(tabs.includes(match?.[2] as Tab) ? (match![2] as Tab) : 'deployments');
    setCreating(false);
  }, [pathname]);
  useEffect(() => {
    const selectLinkedDeployment = () => {
      const id = new URLSearchParams(window.location.search).get('deployment');
      if (id) setDeploymentId(id);
    };
    window.addEventListener('popstate', selectLinkedDeployment);
    return () => window.removeEventListener('popstate', selectLinkedDeployment);
  }, []);
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (
        e.key === '/' &&
        !['INPUT', 'TEXTAREA', 'SELECT'].includes((e.target as HTMLElement).tagName) &&
        !document.querySelector('dialog[open]') &&
        !pathname.startsWith('/docs')
      ) {
        e.preventDefault();
        setCollapsed(false);
        searchInput.current?.focus();
      }
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [pathname]);
  const project = projects.find((p) => p.id === selected);
  const deployment = deployments.find((d) => d.id === deploymentId) ?? deployments[0];
  const deploying = deployments.some((d) => inProgress(d.state));
  const latest = deployments[0];
  useEffect(() => {
    if (!pathname.startsWith('/docs'))
      document.title =
        pathname === '/storage'
          ? 'Storage · ForgeDock'
          : project
            ? `${project.name} · ${tab[0].toUpperCase() + tab.slice(1)} · ForgeDock`
            : 'Projects · ForgeDock';
  }, [project?.name, tab, pathname]);
  const api = useManagementApi(token, setAuthenticated, csrfToken);
  useEffect(() => {
    if (pathname.startsWith('/docs')) return;
    let disposed = false;
    async function restoreSession() {
      try {
        const configResponse = await fetch('/api/auth/config', { credentials: 'same-origin' });
        if (!configResponse.ok) throw new Error('Unable to load sign-in configuration.');
        const config = await configResponse.json();
        const mode = config.mode === 'sso' ? 'sso' : 'token';
        if (disposed) return;
        setAuthMode(mode);
        setAuthProvider(config.provider === 'keycloak' ? 'Keycloak' : '');
        if (mode === 'sso') {
          const response = await fetch('/api/session', {
            credentials: 'same-origin',
            cache: 'no-store',
          });
          if (response.ok) {
            const session = await response.json();
            if (!disposed) {
              setCsrfToken(session.csrfToken);
              setAuthenticated(true);
            }
          } else if (response.status !== 401) throw new Error('Unable to restore your session.');
        }
        if (new URLSearchParams(window.location.search).has('authError'))
          setError(
            'SSO sign-in failed or this account is not allowed. Contact your administrator.',
          );
      } catch (error) {
        if (!disposed) setError((error as Error).message);
      } finally {
        if (!disposed) setAuthLoading(false);
      }
    }
    void restoreSession();
    return () => {
      disposed = true;
    };
  }, [pathname.startsWith('/docs')]);
  async function action(task: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await task();
    } catch (e) {
      const message = e instanceof Error ? e.message : 'Request failed.';
      setError(message);
      notify(message, 'error');
    } finally {
      setBusy(false);
    }
  }
  async function operate(kind: 'Stop' | 'Delete') {
    if (!project) return;
    const operation = await api<{ id: string }>(`/projects/${project.id}/operations`, { kind });
    for (let attempt = 0; attempt < 60; attempt++) {
      const state = await api<{ state: string; error: string | null }>(
        `/operations/${operation.id}`,
      );
      if (state.state === 'Failed') throw new Error(state.error ?? 'Project operation failed.');
      if (state.state === 'Completed') {
        if (kind === 'Delete') openProject(null);
        await refreshProjects();
        return;
      }
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
    throw new Error('Operation is still processing. Refresh to inspect the project state.');
  }
  async function refreshProjects() {
    setProjects(await api<Project[]>('/projects'));
  }
  useEffect(() => {
    if (!authenticated) return;
    let disposed = false;
    async function refresh() {
      try {
        const list = await api<Project[]>('/projects');
        if (!disposed) {
          setProjects(list);
          setProjectsLoading(false);
        }
      } catch (e) {
        if (!disposed) setError((e as Error).message);
      }
    }
    void refresh();
    const interval = setInterval(refresh, 4000);
    return () => {
      disposed = true;
      clearInterval(interval);
    };
  }, [authenticated]);
  useEffect(() => {
    setDeployments([]);
    setLogs([]);
    setDeploymentId(new URLSearchParams(window.location.search).get('deployment'));
    if (!selected || !authenticated) return;
    let disposed = false;
    setLoading(true);
    async function refresh() {
      try {
        const list = await api<Deployment[]>(`/projects/${selected}/deployments`);
        if (!disposed) {
          for (const item of list) {
            const prior = observed.current.get(item.id);
            if (
              prior &&
              inProgress(prior) &&
              ['Failed', 'Running', 'Cancelled'].includes(item.state)
            )
              notify(
                `Deployment ${item.state.toLowerCase()}`,
                item.state === 'Failed' ? 'error' : 'success',
              );
            observed.current.set(item.id, item.state);
          }
          setDeployments(list);
        }
      } catch (e) {
        if (!disposed) setError((e as Error).message);
      } finally {
        if (!disposed) setLoading(false);
      }
    }
    void refresh();
    const interval = setInterval(refresh, 2000);
    return () => {
      disposed = true;
      clearInterval(interval);
    };
  }, [selected, authenticated]);
  useEffect(() => {
    setEnvironment([]);
    setEnvironmentLoading(true);
    if (!selected || !authenticated) return;
    let disposed = false;
    void api<string[]>(`/projects/${selected}/environment`)
      .then((values) => {
        if (!disposed) {
          setEnvironment(values);
          setEnvironmentLoading(false);
        }
      })
      .catch((e) => {
        if (!disposed) {
          setError(e.message);
          setEnvironmentLoading(false);
        }
      });
    return () => {
      disposed = true;
    };
  }, [selected, authenticated]);
  useEffect(() => {
    setLogs([]);
    if (!deployment?.id || !authenticated) return;
    let disposed = false;
    let after = 0;
    async function refresh() {
      try {
        const lines = await api<Log[]>(`/deployments/${deployment!.id}/logs?after=${after}`);
        if (!disposed && lines.length) {
          after = lines[lines.length - 1].id;
          setLogs((old) => [...old, ...lines].slice(-3000));
        }
      } catch (e) {
        if (!disposed) setError((e as Error).message);
      }
    }
    void refresh();
    const interval = setInterval(refresh, 1500);
    return () => {
      disposed = true;
      clearInterval(interval);
    };
  }, [deployment?.id, authenticated]);
  function acceptDeployment(d: Deployment) {
    observed.current.set(d.id, d.state);
    setDeployments((old) => [d, ...old]);
    setDeploymentId(d.id);
    changeTab('deployments');
  }
  function deploy(commitSha?: string) {
    if (!project) return;
    void action(async () => {
      const d = await api<Deployment>(
        `/projects/${project.id}/deployments`,
        commitSha ? { commitSha } : {},
      );
      acceptDeployment(d);
      setCommitDialog(false);
      setCommitInput('');
    });
  }
  function redeploy(d: Deployment) {
    void action(async () =>
      acceptDeployment(await api<Deployment>(`/deployments/${d.id}/redeploy`, {})),
    );
  }
  function rollback(d: Deployment) {
    void (async () => {
      if (
        await confirm({
          title: 'Roll back deployment?',
          message:
            'Restore this retained image and its original environment variables. Traffic switches after health checks pass.',
          label: 'Roll back',
        })
      )
        await action(async () =>
          acceptDeployment(await api<Deployment>(`/deployments/${d.id}/rollback`, {})),
        );
    })();
  }
  function cancelDeployment(d: Deployment) {
    void (async () => {
      if (
        await confirm({
          title: 'Cancel queued deployment?',
          message:
            'This revision will be removed from the deployment queue. The current application keeps serving traffic.',
          label: 'Cancel deployment',
          danger: true,
        })
      )
        await action(async () => {
          await api(`/deployments/${d.id}/cancel`, {}, 'POST');
          setDeployments(await api<Deployment[]>(`/projects/${d.projectId}/deployments`));
        });
    })();
  }
  function deleteDeployment(d: Deployment) {
    void (async () => {
      if (
        await confirm({
          title: 'Delete deployment history?',
          message:
            'Remove this failed or cancelled deployment and its logs. This does not remove retained source files, images, or containers.',
          label: 'Delete entry',
          danger: true,
        })
      )
        await action(async () => {
          await api(`/deployments/${d.id}`, undefined, 'DELETE');
          setDeployments(await api<Deployment[]>(`/projects/${d.projectId}/deployments`));
          setDeploymentId(null);
        });
    })();
  }
  function deleteProject() {
    if (!project) return;
    void (async () => {
      if (
        await confirm({
          title: `Delete ${project.name}?`,
          message:
            'Stop and remove project containers, routes, variables, and deployment history. Compose data volumes are preserved.',
          label: 'Delete project',
          danger: true,
        })
      )
        await action(() => operate('Delete'));
    })();
  }
  const projectStatus =
    latest?.state === 'Failed'
      ? 'Deployment failed'
      : latest?.state === 'Cancelled'
        ? 'Deployment cancelled'
        : latest && inProgress(latest.state)
          ? `Deployment ${latest.state.toLowerCase()}`
          : project?.healthStatus === 'NotDeployed'
            ? 'Not deployed'
            : (project?.healthStatus ?? 'Not deployed');
  if (authLoading && !pathname.startsWith('/docs')) return <Skeleton label="Checking session…" />;
  if (!authenticated)
    return (
      <main className="login">
        <div className="brand">◈ ForgeDock</div>
        <a className="login-docs" href="/docs" onClick={(event) => navigate(event, '/docs')}>
          Documentation ↗
        </a>
        <h1>Your deployment control room.</h1>
        {authMode === 'sso' ? (
          <>
            <p>Sign in securely with {authProvider || 'your organization’s identity provider'}.</p>
            <button onClick={() => window.location.assign('/api/auth/login')}>
              Sign in with SSO
            </button>
          </>
        ) : authMode === 'token' ? (
          <>
            <p>Sign in with your management token. It stays in memory for this session.</p>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void action(async () => {
                  await api('/session');
                  setAuthenticated(true);
                });
              }}
            >
              <label>
                Management token
                <input
                  type="password"
                  autoComplete="off"
                  required
                  value={token}
                  onChange={(e) => setToken(e.target.value)}
                />
              </label>
              <button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
            </form>
          </>
        ) : (
          <p>Sign-in is unavailable. Reload the page to try again.</p>
        )}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
      </main>
    );
  return (
    <div className={'layout ' + (collapsed ? 'sidebar-collapsed' : '')}>
      <aside className="sidebar">
        <div className="sidebar-brand">
          <div className="brand">
            <Icon name="project" size={24} />
            <span>ForgeDock</span>
          </div>
          <button
            className="icon-button secondary collapse-toggle"
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            onClick={() => setCollapsed(!collapsed)}
          >
            <Icon name={collapsed ? 'expand' : 'collapse'} />
          </button>
        </div>
        <span className="caption">WORKSPACE</span>
        <nav className="workspace-nav" aria-label="Main navigation">
          <button
            className={'nav ' + (!selected && !creating && pathname !== '/storage' ? 'active' : '')}
            title="Projects"
            onClick={() => openProject(null)}
          >
            <Icon name="project" />
            <span>Projects</span>
          </button>
        </nav>
        <div className="sidebar-title">
          <span>Your projects</span>
          <button
            aria-label="Create project"
            title="Create project"
            onClick={() => {
              openProject(null);
              setCreating(true);
            }}
          >
            +
          </button>
        </div>
        <div className="project-search">
          <Icon name="search" />
          <input
            ref={searchInput}
            type="search"
            placeholder="Search projects…"
            aria-label="Search projects"
            title="Press / to search projects"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <kbd>/</kbd>
        </div>
        <nav className="project-nav" aria-label="Projects">
          {projectsLoading ? (
            <Skeleton label="Loading projects…" />
          ) : (
            projects
              .filter((p) => p.name.toLowerCase().includes(search.toLowerCase()))
              .map((p) => (
                <button
                  key={p.id}
                  className={'nav ' + (selected === p.id ? 'active' : '')}
                  aria-label={p.name}
                  title={p.name}
                  onClick={() => openProject(p.id)}
                >
                  <Icon name="git" />
                  <span className="project-name">{p.name}</span>
                  <span
                    className={'project-dot ' + p.healthStatus.toLowerCase()}
                    title={p.healthStatus}
                  />
                </button>
              ))
          )}
          {!projectsLoading &&
            search &&
            !projects.some((p) => p.name.toLowerCase().includes(search.toLowerCase())) && (
              <p className="search-empty">No matching projects</p>
            )}
        </nav>
        <nav className="resource-nav" aria-label="Resources">
          <span className="caption">RESOURCES</span>
          <a
            className={'nav ' + (pathname === '/storage' ? 'active' : '')}
            aria-current={pathname === '/storage' ? 'page' : undefined}
            href="/storage"
            onClick={(event) => navigate(event, '/storage')}
          >
            <Icon name="storage" />
            <span>Storage</span>
          </a>
          <a
            className="nav"
            title="Documentation"
            href="/docs"
            onClick={(event) => navigate(event, '/docs')}
          >
            <span aria-hidden="true">▤</span>
            <span>Documentation</span>
            <Icon name="external" size={12} />
          </a>
        </nav>
        <div className="sidebar-footer">
          <ThemeToggle />
          <div className="version">
            ForgeDock v0.1.0 ·{' '}
            <a href="/docs" onClick={(e) => navigate(e, '/docs')}>
              Docs
            </a>{' '}
            ·{' '}
            <a href="https://github.com/nikolliervin/ForgeDock" target="_blank" rel="noreferrer">
              GitHub ↗
            </a>
          </div>
        </div>
        <button
          className="signout"
          title="Sign out"
          onClick={() => {
            void action(async () => {
              if (authMode === 'sso') await api('/auth/logout', {}, 'POST');
              setToken('');
              setCsrfToken(undefined);
              setAuthenticated(false);
              setProjects([]);
            });
          }}
        >
          Sign out
        </button>
      </aside>
      <main>
        <header>
          <nav className="breadcrumbs" aria-label="Breadcrumb">
            <a href="/dashboard" onClick={(event) => navigate(event, '/dashboard')}>
              Projects
            </a>
            {pathname === '/storage' && (
              <>
                <span>/</span>
                <span>Storage</span>
              </>
            )}
            {project && (
              <>
                <span>/</span>
                <a
                  href={`/projects/${project.id}/deployments`}
                  onClick={(event) => navigate(event, `/projects/${project.id}/deployments`)}
                >
                  {project.name}
                </a>
                <span>/</span>
                <span>{tab[0].toUpperCase() + tab.slice(1)}</span>
              </>
            )}
          </nav>
          <span
            className="badge environment-badge"
            title="Applications run on your self-hosted server"
          >
            <span className="status-dot" />
            Self-hosted
          </span>
        </header>
        {error && (
          <div role="alert" className="error">
            {error}
            <button onClick={() => setError('')} aria-label="Dismiss error">
              ×
            </button>
          </div>
        )}
        {pathname === '/storage' ? (
          <Storage api={api} />
        ) : creating ? (
          <NewProject
            api={api}
            busy={busy}
            onCancel={() => setCreating(false)}
            onCreate={(request) =>
              void action(async () => {
                const p = await api<Project>('/projects', request);
                await refreshProjects();
                openProject(p.id);
              })
            }
          />
        ) : project ? (
          <>
            <div className="title-row project-heading">
              <div className="project-heading-info">
                <span className="caption">PROJECT</span>
                <h1>{project.name}</h1>
                <div
                  className={
                    'project-status ' +
                    (latest?.state.toLowerCase() ?? project.healthStatus.toLowerCase())
                  }
                >
                  <span className={deploying ? 'spinner' : 'status-dot'} />
                  <span className="project-status-label">
                    {latest ? 'Latest deployment' : 'Application'}
                  </span>
                  <span className="metadata-divider">·</span>
                  <span className="project-status-state">
                    {latest?.state === 'Failed'
                      ? 'Failed'
                      : latest?.state === 'Cancelled'
                        ? 'Cancelled'
                        : projectStatus}
                  </span>
                  {latest?.state === 'Failed' &&
                    project.activeDeploymentId &&
                    project.healthStatus === 'Running' && (
                      <span className="serving-previous">Serving previous version</span>
                    )}
                </div>
                <div className="repo-metadata">
                  <a
                    href={project.repositoryUrl}
                    target="_blank"
                    rel="noreferrer"
                    title={project.repositoryUrl}
                  >
                    <Icon name="git" />
                    <span>
                      {project.repositoryUrl
                        .replace('https://github.com/', '')
                        .replace(/\.git$/, '')}
                    </span>
                    <Icon name="external" size={12} />
                  </a>
                  <CopyButton value={project.repositoryUrl} label="repository URL" />
                  <span title={`Branch: ${project.branch}`}>
                    <Icon name="branch" />
                    {project.branch}
                  </span>
                  {latest?.commitSha && (
                    <>
                      <code title={latest.commitSha}>{latest.commitSha.slice(0, 7)}</code>
                      <CopyButton value={latest.commitSha} label="latest commit SHA" />
                    </>
                  )}
                  {latest?.commitMessage && (
                    <span className="repo-commit-message" title={latest.commitMessage}>
                      {latest.commitMessage}
                    </span>
                  )}
                </div>
              </div>
              <div className="actions project-actions">
                {project.activeDeploymentId && (
                  <>
                    <button
                      className="secondary"
                      disabled={busy || deploying}
                      title="Restart the active retained version"
                      onClick={() =>
                        void action(async () =>
                          acceptDeployment(
                            await api<Deployment>(`/projects/${project.id}/restart`, {}),
                          ),
                        )
                      }
                    >
                      Restart
                    </button>
                    <button
                      className="secondary"
                      disabled={busy || deploying || project.healthStatus === 'Stopped'}
                      onClick={() => {
                        void (async () => {
                          if (
                            await confirm({
                              title: 'Stop application?',
                              message: 'Stop the application and remove its active route.',
                              label: 'Stop application',
                              danger: true,
                            })
                          )
                            await action(() => operate('Stop'));
                        })();
                      }}
                    >
                      Stop
                    </button>
                  </>
                )}
                <div className="deploy-split">
                  <button
                    disabled={busy || deploying}
                    title={
                      deploying
                        ? 'A deployment is already in progress'
                        : 'Deploy the latest revision of the configured branch'
                    }
                    onClick={() => deploy()}
                  >
                    {deploying || busy ? (
                      <>
                        <span className="spinner" />
                        Deploying…
                      </>
                    ) : (
                      'Deploy'
                    )}
                  </button>
                  <Menu label="Deploy options">
                    <button disabled={busy || deploying} onClick={() => deploy()}>
                      Deploy latest
                    </button>
                    <button
                      disabled={busy || deploying || !deployment}
                      onClick={() => deployment && redeploy(deployment)}
                    >
                      Redeploy selected revision
                    </button>
                    <button disabled={busy || deploying} onClick={() => setCommitDialog(true)}>
                      Deploy specific commit
                    </button>
                  </Menu>
                </div>
                <Menu label="Project actions">
                  <button onClick={() => changeTab('settings')}>Project settings</button>
                  <button
                    disabled={busy || deploying}
                    className="menu-danger"
                    onClick={deleteProject}
                  >
                    Delete project
                  </button>
                </Menu>
              </div>
            </div>
            <div className="panel route">
              <span>APPLICATION ROUTE</span>
              <div className="route-address">
                <a
                  href={`http://${project.id.replaceAll('-', '')}.localhost:8088`}
                  target="_blank"
                  rel="noreferrer"
                >
                  {project.id.replaceAll('-', '')}.localhost:8088 <Icon name="external" size={13} />
                </a>
                <CopyButton
                  value={`http://${project.id.replaceAll('-', '')}.localhost:8088`}
                  label="application URL"
                />
              </div>
              <small>
                {project.activeDeploymentId
                  ? `Health: ${project.healthStatus}`
                  : 'Available after your first successful deployment.'}
              </small>
            </div>
            <nav className="tabs" aria-label="Project sections">
              {(
                [
                  'deployments',
                  'metrics',
                  'domains',
                  'databases',
                  'backups',
                  'releases',
                  'jobs',
                  'previews',
                  'console',
                  'environment',
                  'settings',
                ] as const
              ).map((t) => (
                <button
                  key={t}
                  className={tab === t ? 'active' : 'secondary'}
                  onClick={() => changeTab(t)}
                >
                  {t[0].toUpperCase() + t.slice(1)}
                </button>
              ))}
            </nav>
            {tab === 'jobs' ? (
              <Jobs key={project.id} projectId={project.id} api={api} />
            ) : tab === 'releases' ? (
              <Releases key={project.id} projectId={project.id} api={api} />
            ) : tab === 'console' ? (
              <Suspense fallback={<Skeleton label="Opening terminal…" />}>
                <Console
                  key={`${project.id}:${project.activeDeploymentId}`}
                  projectId={project.id}
                  available={
                    !!project.activeDeploymentId && project.healthStatus !== 'Stopped' && !deploying
                  }
                  api={api}
                />
              </Suspense>
            ) : tab === 'previews' ? (
              <Previews key={project.id} projectId={project.id} branch={project.branch} api={api} />
            ) : tab === 'backups' ? (
              <Backups key={project.id} projectId={project.id} api={api} />
            ) : tab === 'databases' ? (
              <Databases key={project.id} projectId={project.id} api={api} />
            ) : tab === 'metrics' ? (
              <Metrics key={project.id} projectId={project.id} api={api} />
            ) : tab === 'domains' ? (
              <Domains
                key={project.id}
                projectId={project.id}
                api={api}
                action={action}
                busy={busy}
              />
            ) : tab === 'environment' ? (
              <EnvironmentPanel
                project={project}
                api={api}
                action={action}
                busy={busy}
                environment={environment}
                loading={environmentLoading}
                onEnvironment={setEnvironment}
              />
            ) : tab === 'settings' ? (
              <ProjectSettings
                project={project}
                api={api}
                action={action}
                busy={busy}
                onRefresh={refreshProjects}
                onDelete={deleteProject}
              />
            ) : (
              <DeploymentView
                api={api}
                deployments={deployments}
                deployment={deployment}
                logs={logs}
                loading={loading}
                busy={busy}
                onSelect={setDeploymentId}
                onDeploy={() => deploy()}
                onRedeploy={redeploy}
                onRollback={rollback}
                onCancel={cancelDeployment}
                onDelete={deleteDeployment}
              />
            )}
          </>
        ) : (
          <>
            <div className="title-row">
              <div>
                <span className="caption">YOUR WORKSPACE</span>
                <h1>Projects</h1>
                <p>Build, deploy, and keep track of your services.</p>
              </div>
              <button onClick={() => setCreating(true)}>New project</button>
            </div>
            {projects.length ? (
              <div className="project-grid">
                {projects.map((p) => (
                  <ProjectCard key={p.id} project={p} onOpen={() => openProject(p.id)} />
                ))}
              </div>
            ) : (
              <div className="panel empty">
                <h2>Your first deployment starts here.</h2>
                <p>
                  Connect a repository, choose a build method, and follow the build from source to a
                  running service.
                </p>
                <button onClick={() => setCreating(true)}>Create your first project</button>
              </div>
            )}
          </>
        )}
        {commitDialog && (
          <Modal title="Deploy specific commit" onClose={() => setCommitDialog(false)}>
            <p>
              Enter the full 40-character Git commit SHA from this repository. The deployment uses
              your current project settings and environment variables.
            </p>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                deploy(commitInput);
              }}
            >
              <label>
                Commit SHA
                <input
                  autoFocus
                  required
                  pattern="[a-fA-F0-9]{40}"
                  minLength={40}
                  maxLength={40}
                  value={commitInput}
                  onChange={(e) => setCommitInput(e.target.value)}
                  placeholder="Full commit SHA"
                />
              </label>
              <div className="modal-actions">
                <button type="button" className="secondary" onClick={() => setCommitDialog(false)}>
                  Cancel
                </button>
                <button disabled={busy}>Deploy commit</button>
              </div>
            </form>
          </Modal>
        )}
      </main>
    </div>
  );
}
function Root() {
  const [pathname, setPathname] = useState(window.location.pathname);
  useEffect(() => {
    const update = () => setPathname(window.location.pathname);
    window.addEventListener('popstate', update);
    return () => window.removeEventListener('popstate', update);
  }, []);
  const docs = pathname === '/docs' || pathname.startsWith('/docs/');

  return (
    <>
      <div hidden={docs}>
        <App pathname={pathname} />
      </div>
      {docs && (
        <Suspense fallback={<Skeleton label="Loading documentation…" />}>
          <Docs />
        </Suspense>
      )}
    </>
  );
}
createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <UIProvider>
      <Root />
    </UIProvider>
  </React.StrictMode>,
);
