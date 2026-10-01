import { test, expect } from '@playwright/test';

test('root directory persists through creation and settings and variables explain build availability', async ({ page }) => {
  let project: Record<string, unknown> | undefined;
  const writes: Record<string, unknown>[] = [];
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if ((request.method() === 'POST' && path === '/api/projects') || (request.method() === 'PUT' && path.startsWith('/api/projects/'))) {
      const body = request.postDataJSON();
      writes.push(body);
      project = { ...body, id: '00000000-0000-0000-0000-000000000009', activeDeploymentId: null, healthStatus: 'NotDeployed' };
      return route.fulfill({ json: project });
    }
    return route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? (project ? [project] : []) : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test-build-settings-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'New project', exact: true }).click();
  await page.getByLabel('Project name').fill('Monorepo backend');
  await page.getByLabel('Repository URL').fill('https://github.com/example/monorepo');
  await expect(page.getByLabel(/^Root directory/)).toHaveValue('.');
  await page.getByLabel(/^Root directory/).fill('backend');
  await page.getByLabel('Build command').fill('npm run build');
  await page.locator('main').getByRole('button', { name: 'Create project', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Monorepo backend', exact: true })).toBeVisible();
  expect(writes[0].rootDirectory).toBe('backend');
  expect(writes[0].buildCommand).toBe('npm run build');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(page.getByLabel(/^Root directory/)).toHaveValue('backend');
  await page.getByLabel(/^Root directory/).fill('frontend');
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1].rootDirectory).toBe('frontend');
  await page.getByRole('button', { name: 'Environment', exact: true }).click();
  await expect(page.locator('main')).toContainText('Railpack builds also receive saved variables');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(page.getByLabel(/^Root directory/)).toHaveValue('frontend');
  await page.getByLabel('Deployment type').selectOption('Compose');
  await expect(page.getByLabel(/^Root directory/)).toHaveCount(0);
  await page.getByLabel('Public service').fill('web');
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await expect.poll(() => writes.length).toBe(3);
  expect(writes[2].rootDirectory).toBe('.');
});
