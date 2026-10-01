import { test, expect } from '@playwright/test';

test('operator creates, configures, deploys and inspects a real service', async ({ page }) => {
  const token = process.env.ForgeDock__ApiToken;
  if (!token) throw new Error('Run through scripts/with-env.sh to load the management token.');
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill(token);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'New project', exact: true }).click();
  const name = `Browser demo ${Date.now()}`;
  await page.getByLabel('Project name').fill(name);
  await page.getByLabel('Repository URL').fill('https://github.com/docker/welcome-to-docker.git');
  await page.getByLabel('Container port').fill('3000');
  await page.locator('main').getByRole('button', { name: 'Create project', exact: true }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Environment', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('BROWSER_TEST');
  await page.getByLabel('Value', { exact: true }).fill('test-value-no-real-secret');
  await page.getByRole('button', { name: 'Save variable', exact: true }).click();
  await expect(page.getByText('BROWSER_TEST', { exact: true })).toBeVisible();
  await expect(page.locator('main')).not.toContainText('test-value-no-real-secret');
  await page.getByRole('button', { name: 'Deployments', exact: true }).click();
  await page.getByRole('button', { name: 'Deploy', exact: true }).click();
  await expect(page.locator('.history .status').first()).toHaveText('Running', { timeout: 100000 });
  await expect(page.getByLabel('Deployment logs')).toContainText('Stage: Running', { timeout: 10000 });
  const url = await page.locator('.route a').getAttribute('href');
  expect(url).toBeTruthy();
  const deployed = await page.context().request.get(url!);
  expect(deployed.status()).toBe(200);
  expect(await deployed.text()).toContain('<html');
  await page.screenshot({ path: `${process.env.ForgeDock__RuntimePath}/dashboard.png`, fullPage: true });
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Stop application', exact: true }).click();
  await expect(page.locator('.route small')).toContainText('Stopped', { timeout: 15000 });
  const [restartResponse] = await Promise.all([
    page.waitForResponse(response => response.url().endsWith('/restart') && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Restart', exact: true }).click(),
  ]);
  expect(restartResponse.status()).toBe(202);
  const restart = await restartResponse.json();
  await expect(page.locator(`[data-deployment-id="${restart.id}"] .status`)).toHaveText('Running', { timeout: 30000 });
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Delete project', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete project', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible({ timeout: 15000 });
  await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.getByLabel('Management token')).toBeVisible();
  await expect(page.getByLabel('Management token')).toHaveValue('');
});


test('dashboard remains usable on a narrow viewport', async ({ page }) => {
  const token = process.env.ForgeDock__ApiToken;
  if (!token) throw new Error('Load the management token with scripts/with-env.sh.');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill(token);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Docker welcome demo', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Docker welcome demo', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: `${process.env.ForgeDock__RuntimePath}/dashboard-mobile.png`, fullPage: true });
});

test('Compose WordPress opens its installer through the public route', async ({ page }) => {
  const token = process.env.ForgeDock__ApiToken;
  if (!token) throw new Error('Load the management token with scripts/with-env.sh.');
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill(token);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Compose WordPress demo', exact: true }).click();
  await expect(page.locator('.route small')).toContainText('Running');
  const url = await page.locator('.route a').getAttribute('href');
  const app = await page.context().newPage();
  await app.goto(url!);
  expect(new URL(app.url()).port).toBe('8088');
  expect(new URL(app.url()).pathname).toBe('/wp-admin/install.php');
  await expect(app).toHaveTitle(/WordPress/);
  await app.close();
});

test('retained employee Compose demo serves React and its database API', async ({ page }) => {
  const token = process.env.ForgeDock__ApiToken;
  if (!token) throw new Error('Load the management token with scripts/with-env.sh.');
  const projectsResponse = await page.request.get('http://127.0.0.1:5080/api/projects', {
    headers: { Authorization: `Bearer ${token}` },
  });
  const projects = await projectsResponse.json();
  const project = projects.find((p: { repositoryUrl: string }) => p.repositoryUrl === 'https://github.com/nikolliervin/employee-management');
  expect(project.deploymentMode).toBe('Compose');
  const url = `http://${project.id.replaceAll('-', '')}.localhost:8088`;
  await page.goto(url);
  await expect(page.locator('body')).toContainText('Employee');
  for (const resource of ['Employees', 'Departments']) {
    const response = await page.request.get(`${url}/api/v1/${resource}`);
    expect(response.status()).toBe(200);
    expect((await response.json()).isSuccess).toBe(true);
  }
  await page.screenshot({ path: `${process.env.ForgeDock__RuntimePath}/employee-management.png`, fullPage: true });
});
