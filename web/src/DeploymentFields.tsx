import type { Api } from './types';
import { ConfigurationChecker, type ConfigurationCheck } from './ConfigurationChecker';
import { useState } from 'react';
export type DeploymentConfig = {
  deploymentMode: 'Dockerfile' | 'Compose' | 'Auto';
  dockerfile: string;
  rootDirectory: string;
  buildCommand: string;
  startCommand: string;
  composeFile: string;
  composeService: string;
};
export function DeploymentFields({
  project,
  api,
  detectDefaultBranch = false,
}: {
  project?: DeploymentConfig;
  api: Api;
  detectDefaultBranch?: boolean;
}) {
  const [mode, setMode] = useState(project?.deploymentMode ?? 'Auto');
  const [metadata, setMetadata] = useState<ConfigurationCheck>();
  const [composeFile, setComposeFile] = useState(project?.composeFile ?? 'docker-compose.yml');
  const [composeService, setComposeService] = useState(project?.composeService ?? '');
  return (
    <>
      <ConfigurationChecker
        api={api}
        detectDefaultBranch={detectDefaultBranch}
        onResult={setMetadata}
        onApplyMode={() => setMode('Compose')}
        onApplyFile={(file) => {
          setComposeFile(file);
        }}
      />
      <label>
        Deployment type
        <select
          aria-label="Deployment type"
          name="deploymentMode"
          value={mode}
          onChange={(event) => setMode(event.target.value as typeof mode)}
        >
          <option value="Auto">Auto — detect and build your application</option>
          <option value="Dockerfile">Dockerfile — single application</option>
          <option value="Compose">Docker Compose — multiple services</option>
        </select>
      </label>
      {mode !== 'Compose' ? (
        <>
          <label>
            Root directory <span className="field-hint">Relative to the repository</span>
            <input
              name="rootDirectory"
              required
              defaultValue={project?.rootDirectory ?? '.'}
              placeholder="backend"
            />
          </label>
          <label>
            Dockerfile path <span className="field-hint">Relative to the root directory</span>
            <input name="dockerfile" required defaultValue={project?.dockerfile ?? 'Dockerfile'} />
          </label>
        </>
      ) : (
        <>
          <label>
            Compose file path
            {metadata?.composeFiles.length ? (
              <select
                aria-label="Compose file path"
                name="composeFile"
                value={composeFile}
                onChange={(e) => setComposeFile(e.target.value)}
              >
                {!metadata.composeFiles.includes(composeFile) && (
                  <option value={composeFile}>{composeFile} (not found)</option>
                )}
                {metadata.composeFiles.map((file) => (
                  <option key={file} value={file}>
                    {file}
                  </option>
                ))}
              </select>
            ) : (
              <input
                aria-label="Compose file path"
                name="composeFile"
                required
                value={composeFile}
                onChange={(e) => setComposeFile(e.target.value)}
              />
            )}
          </label>
          <label>
            Service exposed through your app URL
            {metadata?.services.length ? (
              <select
                aria-label="Service exposed through your app URL"
                name="composeService"
                required
                value={composeService}
                onChange={(e) => setComposeService(e.target.value)}
              >
                <option value="">Choose a service</option>
                {composeService && !metadata.services.some((s) => s.name === composeService) && (
                  <option value={composeService}>{composeService} (not found)</option>
                )}
                {metadata.services.map((service) => (
                  <option key={service.name} value={service.name}>
                    {service.name}
                    {service.ports.length ? ` · port ${service.ports.join(', ')}` : ''}
                  </option>
                ))}
              </select>
            ) : (
              <input
                aria-label="Service exposed through your app URL"
                name="composeService"
                required
                value={composeService}
                onChange={(e) => setComposeService(e.target.value)}
                placeholder="Check configuration to discover services"
              />
            )}
          </label>
          {metadata && (
            <button type="button" className="secondary" onClick={() => setMetadata(undefined)}>
              Enter settings manually
            </button>
          )}
          <p>
            The public service receives application traffic. Use its internal port below. Other
            services stay on the stack's private networks. Named data volumes persist across
            deployments; updates may briefly interrupt service.
          </p>
        </>
      )}
      {mode === 'Auto' && (
        <>
          <p>
            Uses the Dockerfile above when present. Otherwise Railpack detects your runtime and
            builds the application. Overrides below apply to Railpack builds.
          </p>
          <label>
            Build command <span className="field-hint">Optional · detected automatically</span>
            <input
              name="buildCommand"
              maxLength={4096}
              defaultValue={project?.buildCommand ?? ''}
              placeholder="npm run build"
            />
          </label>
          <label>
            Start command <span className="field-hint">Optional · detected automatically</span>
            <input
              name="startCommand"
              maxLength={4096}
              defaultValue={project?.startCommand ?? ''}
              placeholder="npm start"
            />
          </label>
        </>
      )}
    </>
  );
}
