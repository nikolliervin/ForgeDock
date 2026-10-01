import { test, expect } from '@playwright/test';
test('automatic rollback policy can be configured and its watch state is visible', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000080'; const writes: any[] = [];
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') { writes.push({ path, body: request.postDataJSON() }); return route.fulfill({ json: {} }); }
    return route.fulfill({ json: path.endsWith('/session') ? { name: 'operator' } : path === '/api/projects' ? [{ id, name: 'Rollback app', repositoryUrl: 'https://github.com/example/app', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null }] : path.endsWith('/rollback-policy') ? { autoRollbackEnabled: false, rollbackWindowMinutes: 10, rollbackFailureThreshold: 3, releases: [{ id: 'release-watch', rollbackDeadlineAt: new Date().toISOString(), healthFailureCount: 3, autoRollbackTriggeredAt: new Date().toISOString() }] } : path.endsWith('/resources') ? { cpuLimit: 1, memoryLimitMiB: 512, alerts: [] } : path.endsWith('/notifications') ? { deliveries: [] } : [] });
  });
  await page.goto(`/projects/${id}/settings`); await page.getByLabel('Management token').fill('rollback-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByLabel('Enable automatic rollback').check(); await page.getByLabel('Observation window (minutes)').fill('15'); await page.getByLabel('Consecutive failed health checks').fill('2'); await page.getByRole('button', { name: 'Save rollback policy' }).click();
  await expect.poll(() => writes.some(w => w.path.endsWith('/rollback-policy'))).toBe(true); expect(writes.find(w => w.path.endsWith('/rollback-policy')).body).toEqual({ autoRollbackEnabled: true, rollbackWindowMinutes: 15, rollbackFailureThreshold: 2 });
  await expect(page.getByText('3 failed checks · Rollback queued', { exact: false })).toBeVisible();
});
