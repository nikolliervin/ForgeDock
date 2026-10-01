import { test, expect } from '@playwright/test';
test('storage policy, deletion preview, and confirmed queued cleanup', async ({ page }) => {
  const writes: { path: string; body: any }[] = [];
  const policy = { automaticCleanup: false, retainedDeployments: 5, sourceRetentionDays: 7, logRetentionDays: 30, orphanRetentionDays: 7 };
  let jobs: any[] = [];
  await page.route('**/api/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') { writes.push({ path, body: request.postDataJSON() }); if (path.endsWith('/cleanup')) jobs = [{ id: 'job', state: 'Queued', createdAt: new Date().toISOString(), error: null }]; return route.fulfill({ json: {} }); }
    return route.fulfill({ json: path.endsWith('/session') ? { name: 'operator' } : path === '/api/storage' ? { policy, jobs } : path.endsWith('/preview') ? { runtimeBytes: 1024, freeBytes: 4096, totalBytes: 8192, expiredLogCount: 15, artifacts: [{ kind: 'Source', name: 'old-checkout', sizeBytes: 1024, reason: 'Source retention elapsed' }] } : [] });
  });
  await page.goto('/storage'); await page.getByLabel('Management token').fill('storage-test-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByLabel('Successful deployments retained per project').fill('3'); await page.getByRole('button', { name: 'Save retention policy' }).click();
  expect(writes.find(w => w.path.endsWith('/policy'))?.body.retainedDeployments).toBe(3);
  await expect(page.getByRole('button', { name: 'Run cleanup', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Preview cleanup and disk usage' }).click(); await expect(page.getByText('old-checkout', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Run cleanup', exact: true }).click(); await expect(page.getByRole('dialog')).toContainText('Permanently remove');
  expect(writes.some(w => w.path.endsWith('/cleanup'))).toBe(false);
  await page.getByRole('button', { name: 'Clean up storage', exact: true }).click();
  await expect(page.getByText('Queued ·', { exact: false })).toBeVisible(); expect(writes.find(w => w.path.endsWith('/cleanup'))?.body).toEqual({ confirm: true });
});
