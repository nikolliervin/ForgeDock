import { test, expect } from '@playwright/test';
test('templates fill editable defaults preserve repository details and download starter files', async ({ page }) => {
  const defaults = { deploymentMode: 'Auto', dockerfile: 'Dockerfile', rootDirectory: '.', buildCommand: '', startCommand: '', composeFile: 'compose.yaml', composeService: '', suggestedDatabase: null, healthPath: '/health' };
  let created: any;
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (path.endsWith('/archive')) return route.fulfill({ body: Buffer.from('PK-starter-fixture'), contentType: 'application/zip' });
    if (request.method() === 'POST' && path === '/api/projects') { created = request.postDataJSON(); return route.fulfill({ json: { ...created, id: 'project-created', healthStatus: 'NotDeployed' } }); }
    return route.fulfill({ json: path === '/api/templates' ? [
      { ...defaults, id: 'fastapi', name: 'Python / FastAPI', description: 'Uvicorn starter', containerPort: 8000, startCommand: 'uvicorn main:app --host 0.0.0.0 --port $PORT', suggestedDatabase: 'PostgreSql' },
      { ...defaults, id: 'compose', name: 'Node.js + Redis stack', description: 'Compose starter', containerPort: 8080, deploymentMode: 'Compose', composeService: 'web' }
    ] : path === '/api/session' ? { name: 'operator' } : [] });
  });
  await page.goto('/dashboard'); await page.getByLabel('Management token').fill('template-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'New project', exact: true }).click();
  await page.getByLabel('Project name', { exact: true }).fill('My starter'); await page.getByLabel('Repository URL', { exact: true }).fill('https://github.com/example/starter');
  await page.getByLabel('Branch', { exact: true }).fill('master');
  await page.getByLabel('Project template', { exact: true }).selectOption('fastapi');
  await expect(page.getByLabel('Container port', { exact: true })).toHaveValue('8000');
  await expect(page.getByLabel(/Start command/)).toHaveValue('uvicorn main:app --host 0.0.0.0 --port $PORT');
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download starter ZIP', exact: true }).click();
  expect((await download).suggestedFilename()).toBe('forgedock-fastapi-starter.zip');
  await page.getByLabel('Project template', { exact: true }).selectOption('compose');
  await expect(page.getByLabel('Repository URL', { exact: true })).toHaveValue('https://github.com/example/starter');
  await expect(page.getByLabel('Branch', { exact: true })).toHaveValue('master');
  await expect(page.getByLabel('Public service', { exact: true })).toHaveValue('web');
  await page.getByLabel('Container port', { exact: true }).fill('9000');
  await page.getByLabel('Managed database', { exact: true }).selectOption('SqlServer');
  await expect(page.getByRole('checkbox', { name: /I accept/ })).toHaveAttribute('required', '');
  await page.getByLabel('Managed database', { exact: true }).selectOption('Redis');
  await page.locator('main').getByRole('button', { name: 'Create project', exact: true }).click();
  await expect.poll(() => created?.containerPort).toBe(9000);
  expect(created).toMatchObject({ name: 'My starter', branch: 'master', deploymentMode: 'Compose', composeFile: 'compose.yaml', composeService: 'web', database: 'Redis', healthPath: '/health' });
});
