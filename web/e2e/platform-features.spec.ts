import { test, expect } from '@playwright/test';
test('database backups previews resource settings and notifications are available from the project', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000030', dbid = '00000000-0000-0000-0000-000000000031';
  const project = { id, name: 'Platform app', repositoryUrl: 'https://github.com/example/app', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'NotDeployed', activeDeploymentId: null, rootDirectory: '.', buildCommand: '', startCommand: '' };
  let databases: unknown[] = [], jobs: unknown[] = [], previewsEnabled = false;
  const writes: { path: string; body: any }[] = [];
  await page.route('**/api/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname.replace('/api', '');
    if (request.method() !== 'GET' && path !== '/session') {
      const body = request.postDataJSON(); writes.push({ path, body });
      if (path.endsWith('/databases')) databases = [{ id: dbid, kind: body.kind, state: 'Running', backupIntervalHours: 0 }];
      if (path.endsWith('/schedule')) databases = [{ id: dbid, kind: 'PostgreSql', state: 'Running', backupIntervalHours: body.intervalHours }];
      if (path.endsWith('/backups')) jobs = [{ id: 'backup-one', serviceId: dbid, kind: 'Backup', state: 'Completed', createdAt: new Date().toISOString(), sizeBytes: 1024 }];
      if (path.endsWith('/previews')) previewsEnabled = body.enabled;
      return route.fulfill({ json: {} });
    }
    return route.fulfill({ json: path === '/session' ? { name: 'operator' } : path === '/projects' ? [project] : path.endsWith('/databases') ? databases : path.endsWith('/backups') ? jobs : path.endsWith('/previews') ? { enabled: previewsEnabled, previews: [], domains: [] } : path.endsWith('/resources') ? { cpuLimit: 1, memoryLimitMiB: 512, alertsEnabled: true, alerts: [] } : path.endsWith('/notifications') ? { onSuccess: true, onFailure: true, email: '', slackConfigured: false, discordConfigured: false, smtpConfigured: false, deliveries: [] } : path.endsWith('/webhook') ? { enabled: false, configured: false } : [] });
  });
  await page.goto(`/projects/${id}/databases`);
  await page.getByLabel('Management token').fill('platform-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Add PostgreSQL', exact: true }).click();
  await expect(page.getByText('DATABASE_URL', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Backups', exact: true }).click();
  await page.getByLabel('PostgreSql backup schedule').selectOption('24');
  await page.getByRole('button', { name: 'Back up now', exact: true }).click();
  await page.getByRole('button', { name: 'Restore this backup', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('replaces current database data');
  await page.getByRole('button', { name: 'Restore database', exact: true }).click();
  expect(writes.find(write => write.path.endsWith('/restore'))?.body).toEqual({ confirm: true });
  await page.getByRole('button', { name: 'Previews', exact: true }).click();
  await page.getByRole('button', { name: 'Enable previews', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Disable previews', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByLabel('CPU cores', { exact: true }).fill('0.5'); await page.getByLabel('Memory (MiB)', { exact: true }).fill('256');
  await page.getByRole('button', { name: 'Save resource controls', exact: true }).click();
  expect(writes.find(write => write.path.endsWith('/resources'))?.body).toEqual({ cpuLimit: 0.5, memoryLimitMiB: 256, alertsEnabled: true });
  await page.getByLabel('Slack webhook', { exact: true }).fill('https://hooks.slack.com/services/T/B/secret');
  await page.getByRole('button', { name: 'Save notifications', exact: true }).click();
  expect(writes.find(write => write.path.endsWith('/notifications'))?.body.slackUrl).toContain('/T/B/secret');
});
