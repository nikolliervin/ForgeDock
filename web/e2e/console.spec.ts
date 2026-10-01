import { test, expect } from '@playwright/test';

test('console targets the selected project and renders output, failures and history safely', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000010';
  const commands: string[] = [];
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === `/api/projects/${id}/console`) {
      const body = route.request().postDataJSON();
      expect(Object.keys(body)).toEqual(['command']);
      commands.push(body.command);
      return commands.length === 1 ? route.fulfill({ json: { output: '<script>alert(1)</script>\n\x1b[32m/app\x1b[0m', exitCode: 0, truncated: false } })
        : route.fulfill({ status: 409, json: { error: 'The application container must be running and unpaused.' } });
    }
    return route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? [{ id, name: 'Console app', repositoryUrl: 'https://github.com/example/app', branch: 'main', activeDeploymentId: 'active', healthStatus: 'Running', deploymentMode: 'Auto' }] : [] });
  });
  await page.goto('/dashboard');
  await page.getByLabel('Management token').fill('test-console-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('button', { name: 'Console app', exact: true }).click();
  await page.getByRole('button', { name: 'Console', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/projects/${id}/console$`));
  await page.getByLabel('Shell command').pressSequentially('pwd');
  await page.getByLabel('Shell command').press('Enter');
  await expect(page.getByLabel('Console output')).toContainText('<script>alert(1)</script>');
  await expect.poll(() => commands.length).toBe(1);
  expect(commands).toEqual(['pwd']);
  await page.getByLabel('Shell command').press('ArrowUp');
  await expect(page.getByLabel('Console output')).toContainText('$ pwd');
  await page.getByLabel('Shell command').pressSequentially(' -P');
  await page.getByLabel('Shell command').press('Enter');
  await expect(page.getByLabel('Console output')).toContainText('must be running and unpaused');
  await page.getByLabel('Shell command').press('Control+l');
  await page.getByLabel('Shell command').pressSequentially('clear');
  await page.getByLabel('Shell command').press('Enter');
  expect(commands).toEqual(['pwd', 'pwd -P']);
  await page.screenshot({ path: '/tmp/project-terminal.png' });
});

test('console blocks command entry for a stopped project', async ({ page }) => {
  const id = '00000000-0000-0000-0000-000000000011';
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname;
    return route.fulfill({ json: path === '/api/session' ? { name: 'operator' } : path === '/api/projects' ? [{ id, name: 'Stopped app', repositoryUrl: 'https://github.com/example/app', branch: 'main', activeDeploymentId: 'active', healthStatus: 'Stopped', deploymentMode: 'Auto' }] : [] });
  });
  await page.goto(`/projects/${id}/console`);
  await page.getByLabel('Management token').fill('test-console-token');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Container console', exact: true })).toBeVisible();
  await expect(page.getByLabel('Console output')).toContainText('Container unavailable');
  await expect(page.locator('.terminal-connection')).toContainText('Offline');
  await expect(page.getByRole('button', { name: 'Run command', exact: true })).toHaveCount(0);
});
