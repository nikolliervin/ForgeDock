import { test, expect, type Page } from '@playwright/test';
test.setTimeout(30000);
const project = { id: '00000000-0000-0000-0000-000000000004', name: 'Metrics demo', repositoryUrl: 'https://github.com/example/app', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: '00000000-0000-0000-0000-000000000005', healthStatus: 'Running', deploymentMode: 'Compose', composeFile: 'compose.yml', composeService: 'web', buildCommand: '', startCommand: '' };
function response(fresh = true, empty = false) {
  const end = new Date(), start = new Date(end.getTime() - 3600000);
  return { range: '1h', start: start.toISOString(), end: end.toISOString(), bucketSeconds: 30, fresh: fresh && !empty, latestAt: empty ? null : end.toISOString(), services: empty ? [] : ['web', 'db'], latest: empty ? [] : [{ service: 'web', cpuPercent: 12.5, memoryBytes: 104857600, memoryLimitBytes: 536870912, networkReceivedBytes: 50000, networkSentBytes: 25000, blockReadBytes: 1000, blockWrittenBytes: 0, pids: 4, uptimeSeconds: 7500 }], points: empty ? [] : [0, 30, 60].map((seconds, index) => ({ timestamp: new Date(end.getTime() - 60000 + seconds * 1000).toISOString(), cpuPercent: 10 + index, memoryBytes: 104857600, receivedBytesPerSecond: index ? 1024 : null, sentBytesPerSecond: index ? 512 : null })) };
}
async function setup(page: Page, options: { stale?: boolean; empty?: boolean; error?: boolean } = {}) {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/metrics')) { if (options.error) return route.fulfill({ status: 503, json: { error: 'Metrics temporarily unavailable' } }); return route.fulfill({ json: response(!options.stale, options.empty) }); }
    return route.fulfill({ json: url.pathname === '/api/session' ? { name: 'operator' } : url.pathname === '/api/projects' ? [project] : [] });
  });
  await page.goto('/'); await page.getByLabel('Management token').fill('test-metrics-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Metrics demo' }).first().click(); await page.getByRole('button', { name: 'Metrics', exact: true }).click();
}

test('metrics cards, charts, service usage and filters work', async ({ page }) => {
  await setup(page);
  await expect(page.getByText('Live · sampled every 30 seconds')).toBeVisible();
  await expect(page.getByText('12.50%', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('img', { name: 'CPU usage history' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'Memory usage history' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'Network transfer history' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '2h 5m', exact: true })).toBeVisible();
  const range = page.waitForRequest(r => r.url().includes('/metrics?range=24h'));
  await page.getByLabel('Time range').selectOption('24h'); await range;
  const service = page.waitForRequest(r => r.url().includes('service=db'));
  await page.getByLabel('Service', { exact: true }).selectOption('db'); await service;
  await page.screenshot({ path: '../.runtime/screenshots/metrics-desktop.png', fullPage: true });
});

test('stale metrics are clearly marked and fit mobile layout', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 }); await setup(page, { stale: true });
  await expect(page.getByText(/Metrics are stale/)).toBeVisible();
  await expect(page.getByRole('img', { name: 'CPU usage history' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '../.runtime/screenshots/metrics-mobile.png', fullPage: true });
});

test('empty history guides the user without presenting zero usage', async ({ page }) => {
  await setup(page, { empty: true }); await expect(page.getByText(/Waiting for metrics/)).toBeVisible();
  await expect(page.getByText('History will appear as samples arrive.')).toHaveCount(3);
  await expect(page.getByText('—', { exact: true })).toHaveCount(4);
});

test('metrics errors can be retried', async ({ page }) => {
  const options = { error: true }; await setup(page, options);
  await expect(page.getByRole('alert')).toContainText('Metrics temporarily unavailable');
  options.error = false; await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByText('Live · sampled every 30 seconds')).toBeVisible();
});


test('background polling preserves charts and scroll position', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 600 });
  await page.clock.install();
  let requests = 0;
  await setup(page);
  await expect(page.getByRole('img', { name: 'Network transfer history' })).toBeVisible();
  await page.route('**/api/projects/*/metrics?*', async route => {
    requests++;
    // Hold the response so any loading collapse is observable.
    await new Promise(resolve => setTimeout(resolve, 300));
    const data = response();
    data.latest[0].cpuPercent = 25;
    await route.fulfill({ json: data });
  });
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const scroll = await page.evaluate(() => window.scrollY);
  expect(scroll).toBeGreaterThan(0);
  await page.clock.runFor(4500);
  expect(requests).toBe(0);
  expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
  await page.clock.runFor(6000);
  await expect.poll(() => requests).toBe(1);
  await expect(page.getByRole('img', { name: 'CPU usage history' })).toHaveCount(1);
  await expect(page.getByText('Loading metrics…')).toHaveCount(0);
  expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
  await expect(page.getByRole('cell', { name: '25.00%', exact: true })).toBeVisible();
  expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
});
