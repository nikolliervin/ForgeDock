import type { Api, Action, Project } from './types';
import { Skeleton, useUI } from './ui';
import { EnvironmentImport } from './EnvironmentImport';

export function EnvironmentPanel({
  project,
  api,
  action,
  busy,
  environment,
  loading: environmentLoading,
  onEnvironment: setEnvironment,
}: {
  project: Project;
  api: Api;
  action: Action;
  busy: boolean;
  environment: string[];
  loading: boolean;
  onEnvironment: (names: string[]) => void;
}) {
  const { confirm } = useUI();
  return (
    <section className="panel form">
      <h2>Environment variables</h2>
      <p>
        Values are encrypted at rest and never returned by the API. Deploy again to apply changes.
        Rollback restores the original deployment's variables. Railpack builds also receive saved
        variables, including RAILPACK_* settings. Frontend build variables can become public in the
        generated assets. For Compose, reference saved variables using <code>{'${NAME}'}</code> in
        the Compose file.
      </p>
      {environmentLoading ? (
        <Skeleton label="Loading environment variables…" />
      ) : !environment.length ? (
        <div className="empty compact">
          <h3>No environment variables yet</h3>
          <p>Add application configuration below, or import a .env file.</p>
          <button
            className="secondary"
            onClick={() => document.querySelector<HTMLInputElement>('input[name=key]')?.focus()}
          >
            Add variable
          </button>
        </div>
      ) : (
        environment.map((name) => (
          <div className="variable" key={name}>
            <code>{name}</code>
            <span>••••••••</span>
            <button
              className="secondary"
              disabled={busy}
              onClick={() => {
                void (async () => {
                  if (
                    await confirm({
                      title: `Remove ${name}?`,
                      message:
                        'This applies on the next deployment. Retained versions keep their original variables.',
                      label: 'Remove variable',
                      danger: true,
                    })
                  )
                    await action(async () => {
                      await api(`/projects/${project.id}/environment/${name}`, undefined, 'DELETE');
                      setEnvironment(await api<string[]>(`/projects/${project.id}/environment`));
                    });
                })();
              }}
            >
              Delete
            </button>
          </div>
        ))
      )}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          const form = e.currentTarget;
          const values = new FormData(form);
          void action(async () => {
            const name = String(values.get('key'));
            await api(
              `/projects/${project.id}/environment/${encodeURIComponent(name)}`,
              { value: values.get('value') },
              'PUT',
            );
            setEnvironment(await api<string[]>(`/projects/${project.id}/environment`));
            form.reset();
          });
        }}
      >
        <label>
          Name
          <input name="key" required pattern="[A-Za-z_][A-Za-z0-9_]*" placeholder="DATABASE_URL" />
        </label>
        <label>
          Value
          <input name="value" type="password" autoComplete="off" required />
        </label>
        <button disabled={busy}>Save variable</button>
      </form>
      <EnvironmentImport
        key={project.id}
        projectId={project.id}
        api={api}
        action={action}
        busy={busy}
        onSaved={async () =>
          setEnvironment(await api<string[]>(`/projects/${project.id}/environment`))
        }
      />
    </section>
  );
}
