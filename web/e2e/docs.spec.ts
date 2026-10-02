import { test, expect } from '@playwright/test';

test('documentation is public and every guide supports direct navigation', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/docs');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('From repository');
  const navigation = page.getByRole('navigation', { name: 'Documentation', exact: true });
  const paths = await navigation.getByRole('link').evaluateAll(links => links.map(link => link.getAttribute('href')!));
  expect(new Set(paths).size).toBe(paths.length);
  expect(paths).toEqual(expect.arrayContaining(['/docs', '/docs/quickstart', '/docs/storage-cleanup']));
  for (const path of paths) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(navigation.locator('[aria-current="page"]')).toHaveCount(1);
  }
  expect(errors).toEqual([]);
});

test('search, keyboard shortcut, history, and copy work', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto('/docs');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('From repository');
  await page.keyboard.press('Control+k');
  const search = page.getByRole('textbox', { name: 'Search documentation' });
  await expect(search).toBeFocused();
  await search.fill('railpack');
  await page.getByRole('region', { name: 'Search results' }).getByRole('link', { name: /Automatic builds/ }).click();
  await expect(page).toHaveURL(/docs\/automatic-builds$/);
  await expect(search).toHaveValue('');
  await page.getByRole('button', { name: 'Copy code', exact: true }).first().click();
  await expect(page.getByRole('button', { name: 'Copy code', exact: true }).first()).toHaveText('Copied ✓');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('npm run build');
  await page.goBack();
  await expect(page.getByRole('heading', { level: 1 })).toContainText('From repository');
  await search.fill('there-is-no-such-guide');
  await expect(page.getByText('No pages found.')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(search).toHaveValue('');
});

test('documentation preserves the dashboard session state', async ({ page }) => {
  await page.route('**/api/auth/config', route => route.fulfill({ json: { mode: 'token' } }));
  await page.goto('/dashboard');
  const token = page.getByLabel('Management token');
  await token.fill('a-token-that-stays-in-memory');
  await page.getByRole('link', { name: 'Documentation' }).click();
  await page.getByRole('link', { name: 'Open dashboard' }).click();
  await expect(token).toHaveValue('a-token-that-stays-in-memory');
});

test('mobile navigation and long examples fit the screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/docs');
  const menu = page.getByRole('button', { name: 'Toggle documentation navigation' });
  await menu.click();
  await expect(menu).toHaveAttribute('aria-expanded', 'true');
  await page.getByRole('navigation', { name: 'Documentation', exact: true }).getByRole('link', { name: 'Automatic builds', exact: true }).click();
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: '../.runtime/screenshots/docs-mobile.png', fullPage: true });
});

test('unknown pages provide a working return path', async ({ page }) => {
  await page.goto('/docs/missing-guide');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('This page sailed away');
  await page.getByRole('link', { name: 'Back to documentation' }).click();
  await expect(page).toHaveURL(/\/docs$/);
});

test('desktop documentation overview', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/docs');
  await expect(page.getByRole('navigation', { name: 'On this page' })).toBeVisible();
  expect(await page.locator('.docs-site').evaluate(element => getComputedStyle(element).backgroundColor)).toBe('rgb(12, 16, 22)');
  await page.locator('#how-it-works').evaluate(element => element.scrollIntoView({ block: 'start' }));
  await expect(page.getByRole('navigation', { name: 'On this page' }).locator('[aria-current="location"]')).toHaveText('Built around a simple workflow');
  await page.evaluate(() => window.scrollTo(0, 0));
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: '../.runtime/screenshots/docs-desktop.png', fullPage: true });
});

test('desktop docs navigation stays at the left page edge', async ({ page }) => {
  for (const width of [1366, 1600, 1920]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/docs');
    const sidebar = (await page.getByRole('navigation', { name: 'Documentation', exact: true }).boundingBox())!;
    expect(sidebar.x).toBe(0);
    expect(sidebar.width).toBe(252);
    const article = (await page.locator('.docs-main').boundingBox())!;
    expect(article.x).toBeGreaterThanOrEqual(sidebar.x + sidebar.width);
    expect(article.width).toBeLessThanOrEqual(920);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await page.screenshot({ path: '../.runtime/screenshots/docs-left-navigation.png', fullPage: true });
});

test('homepage opens sign-in and documentation remains accessible', async ({ page }) => {
  await page.route('**/api/auth/config', route => route.fulfill({ json: { mode: 'token' } }));
  const apiRequests: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) apiRequests.push(request.url()); });
  await page.goto('/');
  await expect(page.getByLabel('Management token')).toBeVisible();
  expect(apiRequests.every(url => new URL(url).pathname === '/api/auth/config')).toBe(true);
  await page.getByRole('link', { name: /Documentation/ }).click();
  await expect(page).toHaveURL(/\/docs$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('From repository');
  await page.goBack();
  await expect(page.getByLabel('Management token')).toBeVisible();
});
