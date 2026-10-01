import { test, expect } from '@playwright/test';
test('create a recurring task and queue a manual run with execution history', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000090'; let jobs: any[] = [], runs: any[] = []; const writes: any[] = [];
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') { const body = request.postDataJSON(); writes.push({ path, body }); if (path.endsWith('/jobs')) jobs = [{ ...body, id: 'job-one', nextRunAt: null, lastError: null }]; if (path.endsWith('/run')) runs = [{ id: 'run-one', name: 'Cleanup', state: 'Completed', exitCode: 0, output: 'Task finished', truncated: false, createdAt: new Date().toISOString() }]; return route.fulfill({ json: {} }); }
    return route.fulfill({ json: path.endsWith('/session') ? { name: 'operator' } : path === '/api/projects' ? [{ id, name: 'Jobs app', repositoryUrl: 'https://github.com/example/app', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null }] : path.endsWith('/jobs') ? { jobs, runs } : [] });
  });
  await page.goto(`/projects/${id}/jobs`); await page.getByLabel('Management token').fill('jobs-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByLabel('Job name').fill('Cleanup'); await page.getByLabel('Task command').fill('npm run cleanup'); await page.getByLabel('Repeat interval (minutes)').fill('60'); await page.getByRole('button', { name: 'Add task', exact: true }).click();
  await page.getByRole('button', { name: 'Run Cleanup now', exact: true }).click(); await page.getByText('Cleanup · Completed', { exact: false }).click(); await expect(page.getByText('Task finished', { exact: true })).toBeVisible();
  expect(writes.find(w => w.path.endsWith('/jobs')).body.intervalMinutes).toBe(60); expect(writes.some(w => w.path.endsWith('/run'))).toBe(true);
});
