import { test, expect, type Page } from '@playwright/test';

const project = { id: '00000000-0000-0000-0000-000000000002', name: 'Domain demo', repositoryUrl: 'https://github.com/example/service', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Auto', composeFile: 'compose.yaml', composeService: '', buildCommand: '', startCommand: '' };
const hostname = 'app.example.com';
const pending = { id: '00000000-0000-0000-0000-000000000003', hostname, state: 'PendingDns', verificationRequested: false, dnsVerifiedAt: null as string | null, certificateExpiresAt: null as string | null, certificateTrusted: false, error: 'The TXT ownership record is missing.', dnsRecords: [{ type: 'TXT', name: '_forgedock.' + hostname, value: 'forgedock-verification=abc123' }, { type: 'A', name: hostname, value: '203.0.113.10' }] };
async function setup(page: Page, ready = true) {
  let domains: typeof pending[] = [];
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    if (path.endsWith('/domains') && method === 'POST') {
      const input = route.request().postDataJSON();
      expect(input.hostname).toBe(hostname);
      if (domains.length) { await route.fulfill({ status: 409, json: { error: 'This domain is already attached to a project.' } }); return; }
      domains = [{ ...pending }]; await route.fulfill({ status: 201, json: pending }); return;
    }
    if (path.endsWith('/verify')) {
      domains = [{ ...pending, state: 'Active', dnsVerifiedAt: new Date().toISOString(), certificateExpiresAt: '2027-01-01T00:00:00Z', certificateTrusted: true, error: '' }];
      await route.fulfill({ status: 202, json: {} }); return;
    }
    if (method === 'DELETE') { domains = []; await route.fulfill({ status: 202, json: {} }); return; }
    await route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? [project] : path === '/api/hosting' ? { enabled: ready, ready, target: 'deploy.example.com', addresses: ['203.0.113.10'], setupMessage: ready ? null : 'Custom domains are disabled.', stagingCertificates: false } : path.endsWith('/domains') ? domains : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('navigation', { name: 'Projects', exact: true }).getByRole('button', { name: 'Domain demo' }).click();
  await page.getByRole('button', { name: 'Domains', exact: true }).click();
}

test('disabled public hosting explains setup and keeps local access', async ({ page }) => {
  await setup(page, false);
  await expect(page.getByRole('heading', { name: 'Prepare your server for public domains' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add domain' })).toBeDisabled();
  await expect(page.getByRole('link', { name: /localhost:8088/ })).toBeVisible();
  await page.getByRole('link', { name: 'Read the setup guide' }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Custom domains & HTTPS');
});

test('domain creation, DNS records, clipboard, verification, and removal work', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await setup(page);
  await page.getByLabel('Domain name').fill(hostname);
  await page.getByRole('button', { name: 'Add domain' }).click();
  await expect(page.getByText('Awaiting DNS', { exact: true })).toBeVisible();
  await expect(page.getByText('_forgedock.' + hostname, { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Copy forgedock-verification=abc123', exact: true }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('forgedock-verification=abc123');
  await page.getByRole('button', { name: 'Verify DNS', exact: true }).click();
  await expect(page.getByText('HTTPS active', { exact: true })).toBeVisible();
  await expect(page.getByText(/Certificate expires/)).toBeVisible();
  await page.getByRole('button', { name: 'Remove domain', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Remove domain', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'No custom domains yet' })).toBeVisible();
});

test('duplicate errors preserve the existing domain and mobile layout fits', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setup(page);
  await page.getByLabel('Domain name').fill(hostname);
  await page.getByRole('button', { name: 'Add domain' }).click();
  await page.getByLabel('Domain name').fill(hostname);
  await page.getByRole('button', { name: 'Add domain' }).click();
  await expect(page.getByRole('alert')).toContainText('already attached');
  await expect(page.getByText('Awaiting DNS', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: '../.runtime/screenshots/domains-mobile.png', fullPage: true });
});
