import { useState } from 'react';
export type DeploymentConfig = { deploymentMode: 'Dockerfile' | 'Compose' | 'Auto'; dockerfile: string; rootDirectory: string; buildCommand: string; startCommand: string; composeFile: string; composeService: string };
export function DeploymentFields({ project }: { project?: DeploymentConfig }) {
  const [mode, setMode] = useState(project?.deploymentMode ?? 'Auto');
  return <><label>Deployment type<select name="deploymentMode" value={mode} onChange={event => setMode(event.target.value as typeof mode)}>
    <option value="Auto">Auto — detect and build your application</option><option value="Dockerfile">Dockerfile — single application</option><option value="Compose">Docker Compose — multiple services</option>
  </select></label>{mode !== 'Compose' ? <><label>Root directory <span className="field-hint">Relative to the repository</span><input name="rootDirectory" required defaultValue={project?.rootDirectory ?? '.'} placeholder="backend" /></label><label>Dockerfile path <span className="field-hint">Relative to the root directory</span><input name="dockerfile" required defaultValue={project?.dockerfile ?? 'Dockerfile'} /></label></>
    : <><label>Compose file path<input name="composeFile" required defaultValue={project?.composeFile ?? 'docker-compose.yml'} /></label>
      <label>Public service<input name="composeService" required defaultValue={project?.composeService ?? ''} placeholder="frontend" /></label>
      <p>The public service receives application traffic. Use its internal port below. Other services stay on the stack's private networks. Named data volumes persist across deployments; updates may briefly interrupt service.</p></>}{mode === 'Auto' && <><p>Uses the Dockerfile above when present. Otherwise Railpack detects your runtime and builds the application. Overrides below apply to Railpack builds.</p><label>Build command <span className="field-hint">Optional · detected automatically</span><input name="buildCommand" maxLength={4096} defaultValue={project?.buildCommand ?? ''} placeholder="npm run build" /></label><label>Start command <span className="field-hint">Optional · detected automatically</span><input name="startCommand" maxLength={4096} defaultValue={project?.startCommand ?? ''} placeholder="npm start" /></label></>}</>;
}

