import { useEffect, useRef, useState } from 'react';
import { Skeleton, useUI } from './ui';
import { usePollingResource } from './usePollingResource';
import type { Api } from './types';
import './automation.css';

type Data = {
  applicationName: string | null;
  environmentName: string | null;
  environments: { id: string; name: string; environmentName: string; healthStatus: string }[];
  releases: { id: string; projectId: string; commitSha: string | null; createdAt: string }[];
};

export function Releases({ projectId, api }: { projectId: string; api: Api }) {
  const path = `/projects/${projectId}/releases`;
  const { data, loading, error: loadError, refresh } = usePollingResource<Data>(api, path);
  const [application, setApplication] = useState(''),
    [environment, setEnvironment] = useState('');
  const [selected, setSelected] = useState(''),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  const initialized = useRef(false);
  const { confirm } = useUI();
  useEffect(() => {
    if (data && !initialized.current) {
      setApplication(data.applicationName ?? '');
      setEnvironment(data.environmentName ?? '');
      initialized.current = true;
    }
  }, [data]);
  const releases = data?.releases.filter((r) => r.projectId !== projectId) ?? [];
  const dirty =
    !!data &&
    (application !== (data.applicationName ?? '') || environment !== (data.environmentName ?? ''));
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
  return (
    <div className="automation-page release-page">
      <div className="automation-heading">
        <div>
          <h2>Environment groups and promotion</h2>
          <p>Promote a tested image between staging and production.</p>
        </div>
        <span className="badge">{data?.applicationName ?? 'Ungrouped'}</span>
      </div>
      {(error || loadError) && (
        <p role="alert" className="error">
          {error || loadError}
        </p>
      )}
      {loading ? (
        <Skeleton label="Loading release environments…" rows={4} />
      ) : (
        <div className="automation-grid">
          <section className="panel form automation-form">
            <h3>Application group</h3>
            <p>
              Each environment is an independent project. Group projects that deploy the same
              repository.
            </p>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void run(() =>
                  api(
                    path + '/group',
                    { applicationName: application, environmentName: environment },
                    'PUT',
                  ),
                );
              }}
            >
              <fieldset disabled={busy} className="automation-fields">
                <label>
                  Application group
                  <input
                    maxLength={50}
                    pattern="[a-z0-9-]+"
                    value={application}
                    onChange={(e) => setApplication(e.target.value)}
                    placeholder="storefront"
                  />
                </label>
                <label>
                  Environment name
                  <input
                    maxLength={50}
                    pattern="[a-z0-9-]+"
                    value={environment}
                    onChange={(e) => setEnvironment(e.target.value)}
                    placeholder="staging or production"
                  />
                </label>
                <button disabled={!dirty}>Save environment group</button>
              </fieldset>
            </form>
            <p className="field-hint">
              Leave both fields empty to remove this project from its group. Save changes before
              promoting a release.
            </p>
          </section>
          <div className="automation-content">
            <section className="panel">
              <div className="section-heading">
                <h3>Environments</h3>
                <span className="count">{data?.environments.length ?? 0}</span>
              </div>
              {data?.environments.length ? (
                <div className="release-environments">
                  {data.environments.map((item) => (
                    <article
                      key={item.id}
                      className={'release-environment' + (item.id === projectId ? ' current' : '')}
                    >
                      <div>
                        <strong>{item.environmentName}</strong>
                        {item.id === projectId && <span className="badge">This project</span>}
                      </div>
                      <p>{item.name}</p>
                      <span className={'status ' + item.healthStatus.toLowerCase()}>
                        <span className="status-dot" />
                        {item.healthStatus}
                      </span>
                    </article>
                  ))}
                </div>
              ) : (
                <div className="empty compact">
                  <h3>No environment group yet</h3>
                  <p>Save a group and environment name to connect staging and production.</p>
                </div>
              )}
            </section>
            <section className="panel form release-promotion">
              <h3>Promote into this environment</h3>
              <p>
                Reuse a successful image from another environment. Health checks run before traffic
                switches.
              </p>
              <label>
                Source release
                <select
                  aria-label="Source release"
                  disabled={busy || dirty || !releases.length}
                  value={selected}
                  onChange={(e) => setSelected(e.target.value)}
                >
                  <option value="">
                    {releases.length ? 'Select a release' : 'No retained source releases available'}
                  </option>
                  {releases.map((release) => (
                    <option key={release.id} value={release.id}>
                      {
                        data?.environments.find((item) => item.id === release.projectId)
                          ?.environmentName
                      }{' '}
                      · {release.commitSha?.slice(0, 7) ?? release.id.slice(0, 8)} ·{' '}
                      {new Date(release.createdAt).toLocaleString()}
                    </option>
                  ))}
                </select>
              </label>
              <div className="release-runtime-note">
                <strong>Destination runtime configuration</strong>
                <p>
                  This environment keeps its variables, databases, domains, and resource limits.
                  Build-time values remain part of the selected image.
                </p>
              </div>
              <button
                disabled={busy || dirty || !releases.some((release) => release.id === selected)}
                onClick={() =>
                  void run(async () => {
                    if (
                      await confirm({
                        title: 'Promote this release?',
                        message: `Deploy the selected image into ${data?.environmentName ?? 'this environment'} with its saved runtime configuration. Build-time values stay in the image.`,
                        label: 'Promote release',
                      })
                    )
                      await api(path + '/promote', { sourceDeploymentId: selected, confirm: true });
                  })
                }
              >
                Promote release
              </button>
              <p className="field-hint">
                Available for Auto and Dockerfile apps with the same repository. Compose stacks
                deploy independently.
              </p>
            </section>
          </div>
        </div>
      )}
    </div>
  );
}
