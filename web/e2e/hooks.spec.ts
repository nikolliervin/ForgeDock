import { test, expect } from '@playwright/test';
test('deployment hook settings and recorded results', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000070'; const writes: any[] = [];
  const project = { id, name: 'Hooks app', repositoryUrl: 'https://github.com/example/app', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null };
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') { writes.push({ path, body: request.postDataJSON() }); return route.fulfill({ json: {} }); }
    return route.fulfill({ json: path.endsWith('/session') ? { name: 'operator' } : path === '/api/projects' ? [project] : path.endsWith('/hooks') ? { preDeployCommand: '', postDeployCommand: '', hookTimeoutSeconds: 120, executions: [{ id: 'hook', deploymentId: 'release', phase: 'BeforeRoute', state: 'Completed', exitCode: 0, output: 'Migration completed', truncated: false, startedAt: new Date().toISOString() }] } : path.endsWith('/resources') ? { cpuLimit: 1, memoryLimitMiB: 512, alertsEnabled: false, alerts: [] } : path.endsWith('/notifications') ? { deliveries: [] } : [] });
  });
  await page.goto(`/projects/${id}/settings`); await page.getByLabel('Management token').fill('hooks-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByLabel('Before routing command').fill('npm run migrate'); await page.getByLabel('After routing command').fill('echo released'); await page.getByLabel('Hook timeout (seconds)').fill('60');
  await page.getByRole('button', { name: 'Save deployment hooks' }).click(); await expect.poll(() => writes.some(w => w.path.endsWith('/hooks'))).toBe(true);
  expect(writes.find(w => w.path.endsWith('/hooks')).body).toEqual({ preDeployCommand: 'npm run migrate', postDeployCommand: 'echo released', hookTimeoutSeconds: 60 });
  await page.getByText('BeforeRoute · Completed', { exact: false }).click(); await expect(page.getByText('Migration completed', { exact: true })).toBeVisible();
});
