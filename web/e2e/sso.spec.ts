import { test, expect } from '@playwright/test';

test('SSO sign-in replaces the token field and sends the browser to the login endpoint', async ({
  page,
}) => {
  await page.route('**/api/auth/config', (route) => route.fulfill({ json: { mode: 'sso' } }));
  await page.route('**/api/session', (route) => route.fulfill({ status: 401, json: {} }));
  await page.route('**/api/auth/login', (route) =>
    route.fulfill({ contentType: 'text/html', body: 'Identity provider redirect' }),
  );
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Sign in with SSO' })).toBeVisible();
  await expect(page.getByLabel('Management token')).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign in with SSO' }).click();
  await expect(page).toHaveURL(/\/api\/auth\/login$/);
});

test('SSO restores sessions on reload and signs out using CSRF protection', async ({ page }) => {
  let signedOut = false;
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/auth/config') return route.fulfill({ json: { mode: 'sso' } });
    if (path === '/api/session')
      return route.fulfill({
        status: signedOut ? 401 : 200,
        json: { name: 'Alice', csrfToken: 'test-csrf-token' },
      });
    if (path === '/api/auth/logout') {
      expect(request.method()).toBe('POST');
      expect(request.headers()['x-csrf-token']).toBe('test-csrf-token');
      expect(request.headers()['authorization']).toBeUndefined();
      signedOut = true;
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ json: [] });
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Sign in with SSO' })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Sign in with SSO' })).toBeVisible();
});

test('SSO setup guide explains provider MFA and revocation', async ({ page }) => {
  await page.goto('/docs/sso');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('SSO authentication');
  await expect(page.locator('.docs-main')).toContainText('FIDO2 security key');
  await expect(page.locator('.docs-main')).toContainText('AllowedSubjects__0');
  await expect(page.locator('.docs-main')).toContainText('restart the API to revoke access');
});
