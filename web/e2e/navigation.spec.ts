import { test, expect } from '@playwright/test';

test('documentation lives outside the project list and aligns its brand icon', async ({ page }) => {
  const project = { id: '00000000-0000-0000-0000-000000000001', name: 'Example service', repositoryUrl: 'https://github.com/example/service', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Auto', composeFile: 'compose.yaml', composeService: '', buildCommand: '', startCommand: '' };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    await route.fulfill({ json: path === '/api/projects' ? [project] : path === '/api/session' ? { name: 'operator' } : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test-management-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('button', { name: 'Projects', exact: true })).toBeVisible();
  const projects = page.getByRole('navigation', { name: 'Projects', exact: true });
  await expect(projects.getByRole('button', { name: 'Example service' })).toBeVisible();
  await expect(projects.getByRole('link', { name: 'Documentation' })).toHaveCount(0);
  await page.getByRole('navigation', { name: 'Resources' }).getByRole('link', { name: 'Documentation' }).click();
  const brand = page.locator('.docs-header .docs-brand');
  const alignment = await brand.evaluate(element => {
    const icon = element.querySelector('svg')!.getBoundingClientRect();
    const text = [...element.childNodes].find(node => node.nodeType === Node.TEXT_NODE && node.textContent?.includes('ForgeDock'))!;
    const range = document.createRange(); range.selectNode(text);
    const word = range.getBoundingClientRect();
    return Math.abs((icon.top + icon.bottom) / 2 - (word.top + word.bottom) / 2);
  });
  expect(alignment).toBeLessThan(3);
  await page.getByRole('link', { name: 'Open dashboard' }).click();
  await expect(projects.getByRole('button', { name: 'Example service' })).toBeVisible();
  await page.screenshot({ path: '../.runtime/screenshots/dashboard-navigation.png', fullPage: true });
});
