import { test, expect, type Page } from '@playwright/test';
const project = { id: '00000000-0000-0000-0000-000000000010', name: 'CC', repositoryUrl: 'https://github.com/example/cc', branch: 'master', dockerfile: 'Dockerfile', rootDirectory: '.', containerPort: 8080, healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Auto', composeFile: 'compose.yaml', composeService: '', buildCommand: '', startCommand: '' };
const deployment = { id: '00000000-0000-0000-0000-000000000011', projectId: project.id, state: 'Failed', lastStage: 'HealthChecking', createdAt: new Date(Date.now() - 120000).toISOString(), startedAt: new Date(Date.now() - 119000).toISOString(), finishedAt: new Date(Date.now() - 45000).toISOString(), updatedAt: new Date(Date.now() - 45000).toISOString(), commitSha: '0cf8f4a' + 'b'.repeat(33), commitMessage: 'Fix Docker healthcheck', commitAuthor: 'Irwin', error: 'Application did not pass HTTP health checks within 60 seconds.', rollbackSourceId: null, services: [] };
const lines = [{ id: 1, timestamp: new Date().toISOString(), message: 'Stage: Building', phase: 'Build' }, { id: 2, timestamp: new Date().toISOString(), message: 'npm run build completed', phase: 'Build' }, { id: 3, timestamp: new Date().toISOString(), message: 'Stage: HealthChecking', phase: 'Runtime' }, { id: 4, timestamp: new Date().toISOString(), message: 'Connection refused on port 8080', phase: 'Runtime' }];
async function setup(page: Page, projectCount = 1) {
  const mutations: { path: string; body: unknown }[] = [];
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === 'POST' && path !== '/api/session') { mutations.push({ path, body: route.request().postDataJSON() }); return route.fulfill({ json: { ...deployment, id: '00000000-0000-0000-0000-000000000012', state: 'Queued', error: null } }); }
    return route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? [project, ...Array.from({ length: projectCount - 1 }, (_, i) => ({ ...project, id: String(i), name: `Additional project ${i}` }))] : path.endsWith('/deployments') ? [deployment] : path.endsWith('/logs') ? new URL(route.request().url()).searchParams.get('after') === '0' ? lines : [] : [] });
  });
  await page.goto('/dashboard'); await page.getByLabel('Management token').fill('polish-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('navigation', { name: 'Projects', exact: true }).getByRole('button', { name: 'CC', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Health check failed' })).toBeVisible();
  return mutations;
}
test('deployment summary, failure timeline, log controls and review screenshots', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.setViewportSize({ width: 1366, height: 900 });
  await setup(page);
  await expect(page.locator('.project-status')).toContainText('Failed');
  await expect(page.locator('.summary-meta')).toContainText('1m 14s');
  await expect(page.locator('.deployment-stepper .failure')).toContainText('Health check');
  await expect(page.getByText('Deployment ended', { exact: true })).toBeVisible();
  await page.getByLabel('Search logs').fill('refused');
  await expect(page.getByLabel('Deployment logs')).toContainText('Connection refused');
  await expect(page.getByLabel('Deployment logs')).not.toContainText('npm run build');
  await page.getByLabel('Copy logs', { exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('Connection refused');
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download', exact: true }).click(); expect((await download).suggestedFilename()).toContain(deployment.id);
  await page.getByLabel('Search logs').clear();
  await page.getByRole('button', { name: 'Runtime', exact: true }).click();
  await expect(page.getByLabel('Deployment logs')).not.toContainText('npm run build');
  await page.getByRole('button', { name: 'All', exact: true }).click();
  await page.getByRole('button', { name: 'Clear', exact: true }).click();
  await expect(page.getByLabel('Deployment logs')).toContainText('Log view cleared');
  await page.getByRole('button', { name: 'Restore logs', exact: true }).click();
  await page.getByLabel('Wrap lines').check();
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: '../.runtime/screenshots/dashboard-polish-dark.png', fullPage: true });
  await page.getByRole('button', { name: 'Theme: dark', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.screenshot({ path: '../.runtime/screenshots/dashboard-polish-light.png', fullPage: true });
  for (const width of [1180, 390]) { await page.setViewportSize({ width, height: 844 }); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true); }
});
test('tabs survive refresh and browser history, sidebar search and keyboard navigation work', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: 'Environment', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/projects/${project.id}/environment$`));
  await expect(page).toHaveTitle('CC · Environment · ForgeDock');
  await page.goBack(); await expect(page.getByRole('heading', { name: 'Health check failed' })).toBeVisible();
  await page.goForward(); await expect(page.getByRole('heading', { name: 'Environment variables', exact: true })).toBeVisible();
  await page.reload(); await page.getByLabel('Management token').fill('polish-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Environment variables', exact: true })).toBeVisible();
  await page.locator('h1').click(); await page.keyboard.press('/'); await expect(page.getByLabel('Search projects', { exact: true })).toBeFocused();
  await page.getByLabel('Search projects', { exact: true }).fill('no match'); await expect(page.getByText('No matching projects')).toBeVisible();
  await page.getByLabel('Search projects', { exact: true }).clear(); await page.getByLabel('Collapse sidebar', { exact: true }).click(); await expect(page.locator('.layout')).toHaveClass(/sidebar-collapsed/); await page.getByLabel('Expand sidebar', { exact: true }).click();
});
test('deployment menu submits a specific commit and destructive dialog supports Escape', async ({ page }) => {
  const writes = await setup(page);
  await page.getByLabel('Deploy options', { exact: true }).click(); await page.getByRole('button', { name: 'Deploy specific commit', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible(); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByLabel('Project actions', { exact: true }).click(); await page.getByRole('button', { name: 'Delete project', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Delete CC?'); await page.keyboard.press('Escape'); expect(writes).toHaveLength(0);
  await page.getByLabel('Deploy options', { exact: true }).click(); await page.getByRole('button', { name: 'Deploy specific commit', exact: true }).click();
  await page.getByLabel('Commit SHA', { exact: true }).fill('a'.repeat(40)); await page.getByRole('button', { name: 'Deploy commit', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1); expect(writes[0].body).toEqual({ commitSha: 'a'.repeat(40) }); await expect(page.getByRole('button', { name: 'Deploying…', exact: true })).toBeDisabled();
});

test('long project lists scroll independently without overlapping resources', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 });
  await setup(page, 35);
  const list = page.getByRole('navigation', { name: 'Projects', exact: true });
  const resources = page.getByRole('navigation', { name: 'Resources', exact: true });
  for (const height of [768, 600]) {
    await page.setViewportSize({ width: 1366, height });
    const a = (await list.boundingBox())!, b = (await resources.boundingBox())!;
    expect(a.y + a.height).toBeLessThanOrEqual(b.y);
    expect(await list.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
    await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeInViewport();
    await list.evaluate(element => element.scrollTop = element.scrollHeight);
    await expect(list.getByRole('button', { name: 'Additional project 33', exact: true })).toBeInViewport();
  }
});
