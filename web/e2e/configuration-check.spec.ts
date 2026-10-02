import { expect, test } from '@playwright/test';

test('repository checks offer services and internal ports, then invalidate edited settings', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000001';
  const project = { id, name: 'Voting app', repositoryUrl: 'https://github.com/example/voting', branch: 'main', dockerfile: 'Dockerfile', containerPort: 8080, rootDirectory: '.', healthPath: '/', activeDeploymentId: null, healthStatus: 'NotDeployed', deploymentMode: 'Compose', composeFile: 'docker-compose.yml', composeService: 'gateway', buildCommand: '', startCommand: '' };
  let saved: Record<string, unknown> | undefined;
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/configuration/check') {
      const request = route.request().postDataJSON();
      return route.fulfill({ json: { composeFiles: ['docker-compose.yml'], selectedComposeFile: 'docker-compose.yml', services: [{ name: 'vote', ports: [80] }, { name: 'result', ports: [80, 443] }], issues: request.composeService === 'gateway' ? [{ severity: 'error', message: "Service 'gateway' does not exist. Choose one of the discovered services." }] : request.containerPort !== 80 ? [{ severity: 'error', message: 'Use internal port 80; host ports are not used by ForgeDock.' }] : [] } });
    }
    if (path === `/api/projects/${id}` && route.request().method() === 'PUT') saved = route.request().postDataJSON();
    await route.fulfill({ json: path === '/api/projects' ? [project] : path === `/api/projects/${id}` ? project : path === '/api/session' ? { name: 'operator' } : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('navigation', { name: 'Projects', exact: true }).getByRole('button', { name: 'Voting app' }).click();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: 'Check configuration', exact: true }).click();
  await expect(page.getByText("Service 'gateway' does not exist. Choose one of the discovered services.", { exact: false })).toBeVisible();
  await page.getByLabel('Service exposed through your app URL', { exact: true }).selectOption('vote');
  await expect(page.getByText('Settings changed. Check again to validate them.')).toBeVisible();
  await page.getByRole('button', { name: 'Use port 80', exact: true }).click();
  await expect(page.getByLabel('Port inside the container', { exact: true })).toHaveValue('80');
  await page.getByRole('button', { name: 'Check configuration', exact: true }).click();
  await expect(page.getByText('Repository checks complete')).toBeVisible();
  await page.screenshot({ path: '../.runtime/screenshots/configuration-check.png', fullPage: true });
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await expect.poll(() => saved?.containerPort).toBe(80);
  expect(saved?.composeService).toBe('vote');
  await page.getByLabel('Repository URL', { exact: true }).fill('https://github.com/example/another');
  await expect(page.getByLabel('Service exposed through your app URL', { exact: true })).toHaveJSProperty('tagName', 'INPUT');
});

test('stale inspection responses cannot change discovered choices', async ({ page }) => {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/configuration/check') {
      await new Promise(resolve => setTimeout(resolve, 300));
      return route.fulfill({ json: { composeFiles: ['compose.yaml'], selectedComposeFile: 'compose.yaml', services: [{ name: 'old-service', ports: [80] }], issues: [] } });
    }
    await route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'New project', exact: true }).click();
  await page.getByLabel('Deployment type', { exact: true }).selectOption('Compose');
  await page.getByLabel('Repository URL', { exact: true }).fill('https://github.com/example/old');
  await page.getByRole('button', { name: 'Check configuration', exact: true }).click();
  await page.getByLabel('Repository URL', { exact: true }).fill('https://github.com/example/new');
  await expect(page.getByRole('button', { name: 'Check configuration', exact: true })).toBeEnabled();
  await expect(page.getByText('Repository checks complete')).toHaveCount(0);
  await expect(page.getByRole('option', { name: /old-service/ })).toHaveCount(0);
});

test('leaving repository URL loads dismissible engine recommendations without changing settings', async ({ page }) => {
  let checks = 0;
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/configuration/check') {
      checks++;
      return route.fulfill({ json: { suggestedMode: 'Compose', composeFiles: ['compose.yaml'], selectedComposeFile: 'compose.yaml', services: [{ name: 'vote', ports: [80] }, { name: 'result', ports: [80] }], issues: [{ severity: 'warning', message: 'Choose the service exposed through your app URL.' }] } });
    }
    await route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'New project', exact: true }).click();
  await page.getByLabel('Repository URL', { exact: true }).fill('https://github.com/example/voting');
  await page.getByRole('heading', { name: 'Create a project', exact: true }).click();
  await expect(page.getByText('Engine recommendations', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Deployment type', { exact: true })).toHaveValue('Auto');
  await expect(page.getByLabel('Port inside the container', { exact: true })).toHaveValue('8080');
  await page.getByRole('button', { name: 'Use Docker Compose', exact: true }).click();
  await page.getByRole('button', { name: 'Use compose.yaml', exact: true }).click();
  await page.getByLabel('Service exposed through your app URL', { exact: true }).selectOption('vote');
  await page.getByRole('button', { name: 'Use port 80', exact: true }).click();
  await expect(page.getByLabel('Port inside the container', { exact: true })).toHaveValue('80');
  await page.getByRole('button', { name: 'Dismiss suggestions', exact: true }).click();
  await expect(page.getByText('Engine recommendations', { exact: true })).toHaveCount(0);
  await page.getByLabel('Service exposed through your app URL', { exact: true }).fill('custom-service');
  expect(checks).toBe(1);
});
