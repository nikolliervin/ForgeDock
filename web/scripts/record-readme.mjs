// Actual UI captures with isolated demo responses; no production API requests.
import { chromium } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
const base = process.env.FORGEDOCK_DEMO_URL ?? 'http://127.0.0.1:5173';
const root = resolve(import.meta.dirname, '../..');
const output = resolve(root, 'docs/assets');
const work = resolve(root, '.runtime/readme-recordings');
await mkdir(output, { recursive: true }); await mkdir(work, { recursive: true });
const browser = await chromium.launch({ headless: true });
const id = '00000000-0000-0000-0000-000000000060', source = '00000000-0000-0000-0000-000000000061';
const project = { id, name: 'Storefront · Production', repositoryUrl: 'https://github.com/example/storefront', branch: 'main', deploymentMode: 'Auto', containerPort: 8080, healthPath: '/', healthStatus: 'Running', activeDeploymentId: 'demo-active-release' };
const timestamp = '2026-10-01T12:00:00Z';
const policy = { automaticCleanup: false, retainedDeployments: 5, sourceRetentionDays: 7, logRetentionDays: 30, orphanRetentionDays: 7 };
const group = { applicationName: 'storefront', environmentName: 'production', environments: [{ ...project, environmentName: 'production' }, { id: source, name: 'Storefront · Staging', environmentName: 'staging', healthStatus: 'Running' }], releases: [{ id: 'demo-release', projectId: source, commitSha: 'a7e91c4b20d6', createdAt: timestamp }] };
let jobs = [], cleaned = false;
const artifacts = [
  { kind: 'Image', name: 'forgedock/00000000000000000000000000000060:a7e91c4b20d64723a18b179c2decaeee', sizeBytes: 384 * 1024 ** 2, reason: 'Outside retained deployments or orphan grace period' },
  { kind: 'Source', name: 'b628dc421aee47eb9d8ea018894b46ae', sizeBytes: 86 * 1024 ** 2, reason: 'Source retention elapsed' },
  { kind: 'Source', name: 'c829dc421aee47eb9d8ea018894b46ae', sizeBytes: 41 * 1024 ** 2, reason: 'Orphaned deployment or preview checkout' },
];
async function newPage(path, authenticate = true) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, deviceScaleFactor: 1, reducedMotion: 'reduce', permissions: ['clipboard-read', 'clipboard-write'] });
  const page = await context.newPage();
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.route('**/api/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname;
    let json;
    if (req.method() !== 'GET') {
      if (path.endsWith('/policy')) Object.assign(policy, req.postDataJSON());
      if (path.endsWith('/cleanup')) { jobs = [{ id: 'demo-cleanup', state: 'Queued', createdAt: timestamp, error: null }]; }
      if (path.endsWith('/group')) Object.assign(group, req.postDataJSON());
      json = {};
    } else json = path.endsWith('/session') ? { name: 'Demo operator' } : path === '/api/projects' ? [project] : path === '/api/storage' ? { policy, jobs } : path.endsWith('/preview') ? { runtimeBytes: 6.8 * 1024 ** 3, freeBytes: 171 * 1024 ** 3, totalBytes: 400 * 1024 ** 3, expiredLogCount: cleaned ? 0 : 1248, artifacts: cleaned ? [] : artifacts } : path.endsWith('/releases') ? group : [];
    await route.fulfill({ json });
  });
  await page.goto(base + path);
  if (authenticate) { await page.getByLabel('Management token').fill('documentation-demo-token'); await page.getByRole('button', { name: 'Sign in', exact: true }).click(); }
  await page.addStyleTag({ content: '* { caret-color: transparent !important; }' });
  await page.evaluate(() => {
    const style = document.createElement('style'); style.textContent = '#demo-caption{position:fixed;bottom:18px;left:50%;transform:translateX(-50%);z-index:100000;background:#162237f5;border:1px solid #6987b8;border-radius:9px;color:#f3f6ff;padding:13px 22px;font:500 16px system-ui;box-shadow:0 8px 30px #0006;pointer-events:none;white-space:nowrap}#demo-pointer{position:fixed;width:22px;height:22px;border:2px solid #99b8ff;border-radius:50%;box-shadow:0 0 0 4px #6384ff22;pointer-events:none;z-index:100001;display:none}'; document.head.append(style);
    for (const id of ['demo-caption', 'demo-pointer']) { const node = document.createElement('div'); node.id = id; document.body.append(node); }
  });
  return { page, context, errors };
}
async function record(name, path, steps, authenticate = true) {
  const { page, context, errors } = await newPage(path, authenticate);
  const directory = resolve(work, name); await mkdir(directory, { recursive: true });
  const frames = []; let index = 0;
  async function shot(caption, duration = 1.8) {
    await page.evaluate(text => { document.querySelector('#demo-caption').textContent = text; }, caption);
    await page.waitForTimeout(180);
    const file = resolve(directory, `${String(index++).padStart(3, '0')}.png`);
    await page.screenshot({ path: file }); frames.push({ file, duration });
  }
  async function click(locator) {
    await locator.scrollIntoViewIfNeeded(); const box = await locator.boundingBox();
    if (!box) throw new Error('Missing click target');
    await page.evaluate(({ x, y }) => { const dot = document.querySelector('#demo-pointer'); Object.assign(dot.style, { display: 'block', left: `${x - 11}px`, top: `${y - 11}px` }); }, { x: box.x + box.width / 2, y: box.y + box.height / 2 });
    await locator.click();
  }
  await steps(page, shot, click);
  if (errors.length) throw new Error(errors.join('\n'));
  await page.screenshot({ path: resolve(directory, 'final.png') });
  await context.close();
  const manifest = frames.map(({ file, duration }) => `file '${file}'\nduration ${duration}`).join('\n') + `\nfile '${frames.at(-1).file}'\n`;
  const input = resolve(directory, 'frames.txt'); await writeFile(input, manifest);
  const result = spawnSync('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', input, '-filter_complex', '[0:v]fps=8,scale=1080:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle', '-loop', '0', resolve(output, `${name}.gif`)], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(result.stderr);
  console.log(`${name}.gif: ${frames.length} captured steps`);
}
try {
  await record('storage-cleanup', '/storage', async (page, shot, click) => {
    await page.getByText('1,248', { exact: true }).waitFor();
    await shot('Storage overview · disk usage and eligible artifacts');
    await page.getByLabel('Successful deployments retained per project').fill('3');
    if (await page.getByRole('button', { name: 'Run cleanup', exact: true }).isEnabled()) throw new Error('Unsaved policy must block cleanup');
    await shot('Choose your retention policy · save before cleaning');
    await click(page.getByRole('button', { name: 'Save retention policy' }));
    await page.getByRole('button', { name: 'Run cleanup', exact: true }).waitFor();
    await click(page.getByRole('tab', { name: 'Sources' }));
    await shot('Filter the preview · inspect old source checkouts');
    await click(page.getByRole('tab', { name: 'All' }));
    await click(page.getByRole('button', { name: 'Run cleanup', exact: true }));
    await page.getByRole('dialog').waitFor();
    await shot('Review the confirmation · deletion is permanent', 2.2);
    await click(page.getByRole('button', { name: 'Clean up storage', exact: true }));
    await page.getByText('Queued', { exact: true }).waitFor();
    await shot('Cleanup is queued · follow its status in history');
    jobs[0].state = 'Completed'; jobs[0].resultJson = JSON.stringify([1, 2, 3]); cleaned = true;
    await page.getByText('Completed', { exact: true }).waitFor();
    await page.getByText('Storage is up to date', { exact: true }).waitFor();
    await shot('Cleanup complete · active releases stay protected', 2.4);
  });
  await record('release-promotion', `/projects/${id}/releases`, async (page, shot, click) => {
    await page.getByRole('heading', { name: 'Environment groups and promotion' }).waitFor();
    await shot('One application · independent staging and production');
    await page.getByLabel('Source release', { exact: true }).selectOption('demo-release');
    await shot('Select the tested staging release', 2.2);
    await click(page.getByRole('button', { name: 'Promote release', exact: true }));
    await page.getByRole('dialog').waitFor();
    await shot('Promote the same image · production keeps its runtime configuration', 2.6);
    await click(page.getByRole('dialog').getByRole('button', { name: 'Promote release', exact: true }));
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await shot('Promotion requested · health checks run before routing changes', 2.4);
  });
  await record('documentation-search', '/docs', async (page, shot, click) => {
    await page.getByRole('heading', { level: 1 }).waitFor();
    await shot('Built-in documentation · available without signing in');
    await page.keyboard.press('Control+k');
    const search = page.getByRole('textbox', { name: 'Search documentation' });
    await search.fill('railpack');
    await page.getByRole('region', { name: 'Search results' }).waitFor();
    await shot('Press Ctrl+K · search guides by topic', 2.2);
    await click(page.getByRole('region', { name: 'Search results' }).getByRole('link', { name: /Automatic builds/ }));
    await page.getByRole('heading', { level: 1 }).waitFor();
    await shot('Open the automatic builds guide', 2.2);
    await click(page.getByRole('button', { name: 'Copy code', exact: true }).first());
    await page.getByRole('button', { name: 'Copy code', exact: true }).first().filter({ hasText: 'Copied' }).waitFor();
    if (!(await page.evaluate(() => navigator.clipboard.readText())).includes('npm run build')) throw new Error('Example was not copied');
    await shot('Copy example commands directly from the guide', 2.2);
  }, false);
} finally { await browser.close(); }
