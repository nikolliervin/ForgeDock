import type { Api, Action, Project } from './types';
import { DeploymentFields } from './DeploymentFields';
import { WebhookSettings } from './WebhookSettings';
import { Resources } from './Resources';
import { Notifications } from './Notifications';
import { Hooks } from './Hooks';
import { AutomaticRollback } from './AutomaticRollback';

export function ProjectSettings({
  project,
  api,
  action,
  busy,
  onRefresh: refreshProjects,
  onDelete: deleteProject,
}: {
  project: Project;
  api: Api;
  action: Action;
  busy: boolean;
  onRefresh: () => Promise<void>;
  onDelete: () => void;
}) {
  return (
    <section className="project-settings">
      <h2>Project settings</h2>
      <p>
        Changes apply to future deployments. Work already queued keeps its original configuration.
      </p>
      <div className="settings-grid">
        <div className="settings-column">
          <section className="panel form settings-card settings-build">
            <h3>Build and deployment</h3>
            <form
              key={project.id}
              onSubmit={(e) => {
                e.preventDefault();
                const values = new FormData(e.currentTarget);
                void action(async () => {
                  await api(
                    `/projects/${project.id}`,
                    {
                      name: values.get('name'),
                      repositoryUrl: values.get('repository'),
                      branch: values.get('branch'),
                      dockerfile: values.get('dockerfile') ?? 'Dockerfile',
                      deploymentMode: values.get('deploymentMode'),
                      buildCommand: values.get('buildCommand') ?? '',
                      startCommand: values.get('startCommand') ?? '',
                      rootDirectory: values.get('rootDirectory') ?? '.',
                      composeFile: values.get('composeFile') ?? 'docker-compose.yml',
                      composeService: values.get('composeService') ?? '',
                      containerPort: Number(values.get('port')),
                      healthPath: values.get('health'),
                    },
                    'PUT',
                  );
                  await refreshProjects();
                });
              }}
            >
              <label>
                Name
                <input name="name" defaultValue={project.name} required />
              </label>
              <label>
                Repository URL
                <input name="repository" type="url" defaultValue={project.repositoryUrl} required />
              </label>
              <label>
                Branch
                <input name="branch" defaultValue={project.branch} required />
              </label>
              <DeploymentFields project={project} api={api} />
              <label>
                Port inside the container
                <input
                  name="port"
                  type="number"
                  min={1}
                  max={65535}
                  defaultValue={project.containerPort}
                  required
                />
              </label>
              <label>
                Health path
                <input name="health" defaultValue={project.healthPath} required />
              </label>
              <button disabled={busy}>Save settings</button>
            </form>
          </section>
          <section className="panel form settings-card settings-webhook">
            <WebhookSettings
              key={`webhook:${project.id}`}
              projectId={project.id}
              branch={project.branch}
              api={api}
            />
          </section>
        </div>
        <div className="settings-column">
          <section className="panel form settings-card settings-resources">
            <Resources key={`resources:${project.id}`} projectId={project.id} api={api} />
          </section>
          <section className="panel form settings-card settings-notifications">
            <Notifications key={`notifications:${project.id}`} projectId={project.id} api={api} />
          </section>
          <Hooks key={`hooks:${project.id}`} projectId={project.id} api={api} />
          <AutomaticRollback key={`rollback:${project.id}`} projectId={project.id} api={api} />
        </div>
        <section className="panel settings-danger">
          <div>
            <h3>Delete project</h3>
            <p>
              Stops and removes this project's containers, route, variables, and deployment history.
              Persistent database and Compose volumes are preserved. Retained images, backups, and
              source directories require separate cleanup.
            </p>
          </div>
          <button className="danger" disabled={busy} onClick={deleteProject}>
            Delete project
          </button>
        </section>
      </div>
    </section>
  );
}
