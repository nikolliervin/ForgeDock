// Isolated fixtures for core README walkthroughs. Every API write stays in memory.
export const projectId = '00000000-0000-0000-0000-000000000070';
export const previousId = '00000000-0000-0000-0000-000000000071';
const at = new Date(Date.now() - 60000).toISOString();
function release(id, state, commit, message) {
  return { id, projectId, state, lastStage: state, canRollback: state === 'Running', trigger: 'Manual', commitSha: commit.padEnd(40, '0'), commitMessage: message, commitAuthor: 'Demo developer', createdAt: at, updatedAt: at, startedAt: at, finishedAt: state === 'Running' || state === 'Failed' ? at : null, error: null, rollbackSourceId: null, services: [] };
}
export function coreDemo(mode) {
  const project = { id: projectId, name: 'Storefront', repositoryUrl: 'https://github.com/example/storefront.git', branch: 'main', dockerfile: 'Dockerfile', rootDirectory: '.', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null, deploymentMode: 'Auto', buildCommand: '', startCommand: '', composeFile: 'compose.yaml', composeService: '' };
  let exists = mode !== 'create';
  const names = [];
  const deployments = [];
  const logs = new Map();
  const writes = [];
  function line(d, message, phase = 'Build') { const lines = logs.get(d.id) ?? []; lines.push({ id: lines.length + 1, timestamp: at, message, phase }); logs.set(d.id, lines); }
  if (mode === 'rollback') {
    const previous = release(previousId, 'Running', 'c21ab93', 'Add storefront catalog');
    const failed = release('00000000-0000-0000-0000-000000000072', 'Failed', 'e40bf82', 'Update storefront configuration');
    failed.lastStage = 'HealthChecking'; failed.error = 'Application did not pass HTTP health checks within 60 seconds.';
    deployments.push(failed, previous); project.activeDeploymentId = previous.id; project.healthStatus = 'Running';
    line(failed, 'Stage: HealthChecking', 'Runtime'); line(failed, 'Health check failed: connection refused on port 8080', 'Runtime');
    line(previous, 'Stage: Running', 'Runtime'); line(previous, 'Storefront listening on port 8080', 'Runtime');
  }
  function advance(state) {
    const d = deployments[0]; if (!d) throw new Error('No demo deployment queued');
    d.state = state; d.lastStage = state; d.updatedAt = new Date().toISOString();
    line(d, `Stage: ${state}`, ['Starting', 'HealthChecking', 'Routing', 'Running'].includes(state) ? 'Runtime' : 'Build');
    if (state === 'Building') { line(d, 'Railpack detected Node.js application'); line(d, 'npm ci completed'); line(d, 'npm run build completed'); }
    if (state === 'HealthChecking') line(d, 'GET / → 200 OK', 'Runtime');
    if (state === 'Running') { d.finishedAt = new Date().toISOString(); d.canRollback = true; project.activeDeploymentId = d.id; project.healthStatus = 'Running'; line(d, 'Storefront listening on port 8080', 'Runtime'); line(d, 'GET /products → 200 OK', 'Runtime'); }
  }
  async function route(route) {
    const req = route.request(), url = new URL(req.url()), path = url.pathname, method = req.method(); let json = [];
    if (method !== 'GET') {
      const body = req.postDataJSON(); writes.push({ path, body });
      if (path === '/api/session') json = { name: 'Demo operator' };
      else if (path === '/api/projects') { Object.assign(project, body); exists = true; json = project; }
      else if (path.includes('/environment/')) { const name = decodeURIComponent(path.split('/').at(-1)); if (!names.includes(name)) names.push(name); json = {}; }
      else if (path.endsWith('/deployments') || path.endsWith('/rollback')) {
        const rollback = path.endsWith('/rollback');
        const d = release('00000000-0000-0000-0000-000000000073', 'Queued', rollback ? 'c21ab93' : 'a7e91c4', rollback ? 'Restore storefront catalog' : 'Ship storefront catalog');
        if (rollback) d.rollbackSourceId = previousId;
        deployments.unshift(d); line(d, 'Stage: Queued'); json = d;
      } else throw new Error(`Unexpected demo mutation: ${method} ${path}`);
    } else if (path === '/api/projects') json = exists ? [project] : [];
    else if (path.endsWith('/deployments')) json = deployments;
    else if (path.endsWith('/environment')) json = names;
    else if (path.endsWith('/logs')) json = (logs.get(path.split('/')[3]) ?? []).filter(l => l.id > Number(url.searchParams.get('after') ?? 0));
    await route.fulfill({ json });
  }
  return { route, advance, writes, names, deployments };
}
