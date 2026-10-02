import { test, expect } from '@playwright/test';

async function openStack(page: import('@playwright/test').Page, fail = false) {
  const id = '00000000-0000-0000-0000-000000000001';
  const project = { id, name: 'Atlas stack', repositoryUrl: 'https://github.com/example/atlas', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: id, healthStatus: 'Healthy', deploymentMode: 'Compose', composeFile: 'compose.yaml', composeService: 'web', buildCommand: '', startCommand: '' };
  const deployment = { id, projectId: id, state: 'Healthy', commitSha: 'a'.repeat(40), createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), error: null, rollbackSourceId: null, services: [
    { name: 'web', state: 'running', health: 'healthy', image: 'atlas/web:v1' },
    { name: 'api', state: 'running', health: 'healthy', image: 'atlas/api:v1' },
    { name: 'db', state: 'running', health: 'unhealthy', image: 'postgres:17' },
    { name: 'worker', state: 'exited', health: null, image: 'atlas/worker:v1' },
  ] };
  let failures = fail ? Infinity : 0;
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/topology') && failures-- > 0) return route.fulfill({ status: 500, json: { title: 'Unavailable' } });
    await route.fulfill({ json: path === '/api/projects' ? [project] : path === '/api/session' ? { name: 'operator' } : path.endsWith('/topology') ? {
      entryService: 'web', services: [
        { name: 'web', dependencies: ['api'], networks: ['ingress', 'default'], volumes: [] },
        { name: 'api', dependencies: ['db'], networks: ['default'], volumes: [] },
        { name: 'db', dependencies: ['api'], networks: ['default'], volumes: ['data'] },
        { name: 'worker', dependencies: [], networks: ['default'], volumes: [] },
      ],
    } : path === `/api/projects/${id}/deployments` ? [deployment] : path === `/api/deployments/${id}` ? deployment : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('navigation', { name: 'Projects', exact: true }).getByRole('button', { name: 'Atlas stack' }).click();
  await expect(page.locator('.stack-node')).toHaveCount(4);
  return () => { failures = 0; };
}

test('map shows dependencies, health, storage, networks and list with keyboard selection', async ({ page }) => {
  await openStack(page);
  await expect(page.locator('.stack-canvas svg > path')).toHaveCount(3);
  await expect(page.getByText('4 services · 2 need attention')).toBeVisible();
  await page.locator('.stack-node').filter({ hasText: 'db' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.stack-details')).toContainText('postgres:17');
  await expect(page.locator('.stack-details')).toContainText('data');
  await page.getByLabel('Networks', { exact: true }).check();
  await expect(page.locator('.stack-networks')).toContainText('ingress');
  await page.getByRole('button', { name: 'Reset zoom' }).click();
  await page.getByRole('button', { name: 'Zoom in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Reset zoom' })).toHaveText('120%');
  await page.getByRole('button', { name: 'Reset zoom' }).click();
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.screenshot({ path: '../.runtime/screenshots/stack-map.png', fullPage: true });
  await page.getByRole('button', { name: 'List', exact: true }).click();
  await expect(page.locator('.stack-map tbody tr')).toHaveCount(4);
  await page.getByRole('button', { name: 'Map', exact: true }).click();
  await expect(page.locator('.stack-details')).toContainText('postgres:17');
});

test('map retries failed metadata and fits a mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const recover = await openStack(page, true);
  await expect(page.getByRole('alert')).toContainText('Connections could not load');
  recover();
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.stack-canvas svg > path')).toHaveCount(3);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await page.screenshot({ path: '../.runtime/screenshots/stack-map-mobile.png', fullPage: true });
});
