import { test, expect } from '@playwright/test';
test('environment grouping and confirmed release promotion', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000060', source = '00000000-0000-0000-0000-000000000061';
  const project = { id, name: 'Production', repositoryUrl: 'https://github.com/example/app', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null };
  let group: any = { applicationName: null, environmentName: null, environments: [], releases: [] }; const writes: any[] = [];
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') { const body = request.postDataJSON(); writes.push({ path, body }); if (path.endsWith('/group')) group = { ...body, environments: [{ id, name: 'Production', environmentName: 'production', healthStatus: 'NotDeployed' }, { id: source, name: 'Staging', environmentName: 'staging', healthStatus: 'Running' }], releases: [{ id: 'release-one', projectId: source, commitSha: 'abcdef123456', createdAt: new Date().toISOString() }] }; return route.fulfill({ json: {} }); }
    return route.fulfill({ json: path.endsWith('/session') ? { name: 'operator' } : path === '/api/projects' ? [project] : path.endsWith('/releases') ? group : [] });
  });
  await page.goto(`/projects/${id}/releases`); await page.getByLabel('Management token').fill('release-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByLabel('Application group', { exact: true }).fill('my-app'); await page.getByLabel('Environment name', { exact: true }).fill('production'); await page.getByRole('button', { name: 'Save environment group' }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByLabel('Source release', { exact: true }).selectOption('release-one');
  await page.getByLabel('Application group', { exact: true }).fill('unsaved-group');
  await expect(page.getByRole('button', { name: 'Promote release', exact: true })).toBeDisabled();
  await page.getByLabel('Application group', { exact: true }).fill('my-app'); await page.getByRole('button', { name: 'Promote release', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('production'); expect(writes.some(w => w.path.endsWith('/promote'))).toBe(false);
  await page.getByRole('dialog').getByRole('button', { name: 'Promote release', exact: true }).click();
  await expect.poll(() => writes.some(w => w.path.endsWith('/promote'))).toBe(true); expect(writes.find(w => w.path.endsWith('/promote')).body).toEqual({ sourceDeploymentId: 'release-one', confirm: true });
});
