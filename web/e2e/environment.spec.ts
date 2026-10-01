import { test, expect } from '@playwright/test';
test.setTimeout(30000);
const project = { id: '00000000-0000-0000-0000-000000000008', name: 'Environment demo', repositoryUrl: 'https://github.com/example/app', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Auto', composeFile: 'compose.yml', composeService: '', buildCommand: '', startCommand: '' };
test('individual variables and protected bulk imports work without revealing saved values', async ({ page }) => {
  let names = ['PORT']; let savedContent = '', overwrite = false;
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method();
    if (path.endsWith('/environment') && method === 'PUT') {
      const body = route.request().postDataJSON(); savedContent = body.content; overwrite = body.overwrite;
      if (!overwrite) return route.fulfill({ status: 409, json: { error: 'Some variables already exist. Enable replacement to update them.' } });
      names = ['PORT', 'DATABASE_URL', 'NODE_ENV']; return route.fulfill({ json: { saved: 3, names } });
    }
    if (path.endsWith('/environment/NEW_VAR') && method === 'PUT') { expect(route.request().postDataJSON().value).toBe('individual-secret'); names.push('NEW_VAR'); return route.fulfill({ status: 204 }); }
    return route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? [project] : path.endsWith('/environment') ? names : [] });
  });
  await page.goto('/dashboard'); await page.getByLabel('Management token').fill('test-env-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('navigation', { name: 'Projects', exact: true }).getByRole('button', { name: 'Environment demo' }).click();
  await page.getByRole('button', { name: 'Environment', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('NEW_VAR'); await page.getByLabel('Value', { exact: true }).fill('individual-secret'); await page.getByRole('button', { name: 'Save variable', exact: true }).click();
  await expect(page.getByText('NEW_VAR', { exact: true })).toBeVisible();
  await page.getByText('Import from .env', { exact: true }).click();
  const content = 'PORT=8080\nDATABASE_URL="bulk-secret"\nNODE_ENV=production';
  await page.getByLabel('Environment file', { exact: true }).fill(content); await page.getByRole('button', { name: 'Save variables', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Enable replacement'); await expect(page.getByLabel('Environment file', { exact: true })).toHaveValue(content);
  await page.getByLabel('Replace variables that already exist').check(); await page.getByRole('button', { name: 'Save variables', exact: true }).click();
  expect(savedContent).toBe(content); expect(overwrite).toBe(true);
  await expect(page.getByRole('status')).toContainText('Saved 3 variables'); await expect(page.getByLabel('Environment file', { exact: true })).toHaveValue('');
  await expect(page.getByText('DATABASE_URL', { exact: true })).toBeVisible(); await expect(page.getByText('bulk-secret', { exact: true })).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 }); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
