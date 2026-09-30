import { test, expect } from '@playwright/test';

test('project auto-deploy setup reveals a secret once and supports disabling and rotation', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000020';
  const project = { id, name: 'Webhook app', repositoryUrl: 'https://github.com/example/private.git', branch: 'release/v2', dockerfile: 'Dockerfile', containerPort: 8080, healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Auto', rootDirectory: '.', buildCommand: '', startCommand: '', composeFile: 'compose.yaml', composeService: '' };
  const path = `/api/projects/${id}/webhook`;
  let settings = { enabled: false, configured: false, path: `/api/webhooks/github/${id}`, publicUrl: `https://hooks.example.com/api/webhooks/github/${id}`, secret: null as string | null, lastDelivery: null as null | { event: string; status: string; receivedAt: string; deploymentId: string | null } };
  const writes: unknown[] = [];
  await page.route('**/api/**', async route => {
    const request = route.request(), requestPath = new URL(request.url()).pathname;
    if (requestPath === path && request.method() === 'PUT') {
      expect(request.headers().authorization).toBe('Bearer test-webhook-token');
      const body = request.postDataJSON(); writes.push(body);
      const initial = !settings.configured;
      settings = { ...settings, configured: true, enabled: body.enabled, secret: null };
      return route.fulfill({ json: { ...settings, secret: initial ? 'first-webhook-secret' : null } });
    }
    if (requestPath === path + '/rotate-secret') {
      writes.push('rotate');
      return route.fulfill({ json: { ...settings, secret: 'rotated-webhook-secret' } });
    }
    if (requestPath === path) return route.fulfill({ json: settings });
    return route.fulfill({ json: requestPath === '/api/session' ? { name: 'operator' } : requestPath === '/api/projects' ? [project] : [] });
  });
  await page.goto(`/projects/${id}/settings`);
  await page.getByLabel('Management token').fill('test-webhook-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const section = page.getByRole('region', { name: 'GitHub auto-deploy' });
  await expect(section).toContainText('release/v2');
  await section.getByRole('button', { name: 'Enable auto-deploy', exact: true }).click();
  await expect(section.getByLabel('Webhook payload URL', { exact: true })).toHaveValue(settings.publicUrl);
  await section.getByLabel('Webhook payload URL', { exact: true }).fill('https://my-tunnel.trycloudflare.com');
  await section.getByLabel('Webhook payload URL', { exact: true }).press('Tab');
  await expect(section.getByLabel('Webhook payload URL', { exact: true })).toHaveValue(`https://my-tunnel.trycloudflare.com/api/webhooks/github/${id}`);
  await expect(section.getByLabel('Webhook secret', { exact: true })).toHaveValue('first-webhook-secret');
  await expect(section).toContainText('Just the push event');
  await page.screenshot({ path: '/tmp/github-webhook-settings.png', fullPage: true });
  await section.getByRole('button', { name: 'Hide secret', exact: true }).click();
  await expect(section.getByLabel('Webhook secret', { exact: true })).toHaveCount(0);
  await section.getByRole('button', { name: 'Disable auto-deploy', exact: true }).click();
  await expect(section.getByRole('button', { name: 'Enable auto-deploy', exact: true })).toBeVisible();
  await section.getByRole('button', { name: 'Enable auto-deploy', exact: true }).click();
  await expect(section.getByLabel('Webhook secret', { exact: true })).toHaveCount(0);
  await section.getByRole('button', { name: 'Rotate webhook secret', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Rotate secret', exact: true }).click();
  await expect(section.getByLabel('Webhook secret', { exact: true })).toHaveValue('rotated-webhook-secret');
  expect(writes).toEqual([{ enabled: true }, { enabled: false }, { enabled: true }, 'rotate']);
  settings = { ...settings, lastDelivery: { status: 'Queued', event: 'push', receivedAt: new Date().toISOString(), deploymentId: 'deployment' } };
  await page.getByRole('button', { name: 'Environment', exact: true }).click();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(section.getByLabel('Webhook secret', { exact: true })).toHaveCount(0);
  await expect(section).toContainText('Deployment queued');
  await expect(section.getByLabel('Webhook payload URL', { exact: true })).toHaveValue(`https://my-tunnel.trycloudflare.com/api/webhooks/github/${id}`);
});
