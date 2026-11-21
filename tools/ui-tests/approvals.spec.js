// The permission prompt island: a compact card at the top of the panel, newest request first, with arrows (or a
// swipe) through the queue and a tap to open the details. The widgets underneath stay visible and usable, and a
// request that arrives is announced once with a short sound (Settings › Display can turn the sound off).
const { test, expect } = require('@playwright/test');

const PROMPTS = [
  { tool: 'Run a command', detail: 'git push origin main --force-with-lease', description: 'Push the rebased branch', project: 'api-gateway', cwd: '/work/api-gateway' },
  { tool: 'Edit a file', detail: 'src/routes/billing.ts\n\n  export const limit = 120', description: '', project: 'docs-site' },
  { tool: 'Fetch a web page', detail: 'https://docs.github.com/en/rest/pulls', description: '', project: 'blog-engine' },
];

// Counts the notes the panel plays through Web Audio, so a test can tell whether a request was announced.
async function countTones(page) {
  await page.addInitScript(() => {
    window.__tones = 0;
    const Real = window.AudioContext;
    if (!Real) return;
    window.AudioContext = class extends Real {
      createOscillator() { window.__tones++; return super.createOscillator(); }
    };
  });
}

async function open(page, display = {}) {
  await page.goto('/panel/');
  await page.evaluate((display) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || '{}');
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, night: { enabled: false }, display: { ...(cfg.display || {}), ...display } }));
  }, display);
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
}

async function askAll(page) {
  // one after another with distinct times, so "newest" is the last one asked
  await page.evaluate((prompts) => {
    const t0 = Date.now() - 10_000;
    prompts.forEach((p, i) => window.SidelightDemo.ask({ ...p, createdAt: t0 + i * 1000 }));
  }, PROMPTS);
}

test('several requests share one island, newest first, and the widgets stay usable', async ({ page }) => {
  await open(page);
  await askAll(page);
  const island = page.locator('#approval');
  await expect(island).toBeVisible();
  await expect(page.locator('#apCount')).toHaveText('1 of 3');
  await expect(page.locator('#apTool')).toHaveText('Fetch a web page');
  await expect(page.locator('#apBrief')).toHaveText('https://docs.github.com/en/rest/pulls');
  await expect(island).toHaveClass(/stacked/);

  // a small card at the top, not a sheet over the page
  const box = await island.boundingBox();
  const vp = page.viewportSize();
  expect(box.y).toBeLessThan(20);
  expect(box.height).toBeLessThan(vp.height / 2);
  // what lies below the island is the page itself, which still takes taps
  const below = await page.evaluate(({ x, y }) => {
    const el = document.elementFromPoint(x, y);
    return { inIsland: !!el?.closest('#approval'), inPage: !!el?.closest('#pager, .page, .w') };
  }, { x: vp.width / 2, y: vp.height - 40 });
  expect(below).toEqual({ inIsland: false, inPage: true });
});

test('the arrows and a swipe move through the queue, and answering one keeps the rest', async ({ page }) => {
  await open(page);
  await askAll(page);
  await expect(page.locator('#apCount')).toHaveText('1 of 3');
  await expect(page.locator('#apPrev')).toBeDisabled();

  await page.locator('#apNext').click();
  await expect(page.locator('#apCount')).toHaveText('2 of 3');
  await expect(page.locator('#apTool')).toHaveText('Edit a file');

  // a swipe to the left on the summary shows the next older request
  const sum = await page.locator('#apSum').boundingBox();
  const y = sum.y + sum.height / 2;
  await page.mouse.move(sum.x + sum.width * 0.8, y);
  await page.mouse.down();
  await page.mouse.move(sum.x + sum.width * 0.2, y, { steps: 6 });
  await page.mouse.up();
  await expect(page.locator('#apCount')).toHaveText('3 of 3');
  await expect(page.locator('#apTool')).toHaveText('Run a command');
  await expect(page.locator('#apNext')).toBeDisabled();
  await expect(page.locator('#approval')).not.toHaveClass(/open/); // the swipe did not open the card too

  await page.locator('#apPrev').click();
  await expect(page.locator('#apCount')).toHaveText('2 of 3');

  await page.locator('#apDeny').click();
  await expect(page.locator('#toast')).toContainText('Edit a file');
  // the answered one is gone; the next older one takes its place and the others still wait
  await expect(page.locator('#apCount')).toHaveText('2 of 2');
  await expect(page.locator('#apTool')).toHaveText('Run a command');
  await page.locator('#apAllow').click();
  await expect(page.locator('#apCount')).toHaveText('');
  await expect(page.locator('#apTool')).toHaveText('Fetch a web page');
  await page.locator('#apAllow').click();
  await expect(page.locator('#approval')).toBeHidden();
});

test('a tap on the summary opens the full command and folds it again', async ({ page }) => {
  await open(page);
  await page.evaluate((p) => window.SidelightDemo.ask(p), PROMPTS[1]);
  const detail = page.locator('#apDetail');
  await expect(page.locator('#approval')).toBeVisible();
  await expect(detail).toBeHidden();
  await expect(page.locator('#apSum')).toHaveAttribute('aria-expanded', 'false');
  await page.locator('#apSum').click();
  await expect(detail).toBeVisible();
  await expect(detail).toContainText('export const limit = 120');
  await expect(page.locator('#apProject')).toHaveText('docs-site');
  await expect(page.locator('#apSum')).toHaveAttribute('aria-expanded', 'true');
  await page.locator('#apSum').click();
  await expect(detail).toBeHidden();
});

test('a new request is announced once with a short sound', async ({ page }) => {
  await countTones(page);
  await open(page);
  await page.evaluate(() => { window.__tones = 0; });
  await askAll(page);
  await expect(page.locator('#apCount')).toHaveText('1 of 3');
  await expect.poll(() => page.evaluate(() => window.__tones)).toBeGreaterThan(0);
  const heard = await page.evaluate(() => window.__tones);
  // moving through the queue and the list refresh are not news
  await page.locator('#apNext').click();
  await expect(page.locator('#apCount')).toHaveText('2 of 3');
  await page.evaluate(() => window.SidelightDemo.emit('approval', {}));
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.__tones)).toBe(heard);
});

test('the sound can be turned off', async ({ page }) => {
  await countTones(page);
  await open(page, { alertSound: false });
  await page.evaluate(() => { window.__tones = 0; });
  await askAll(page);
  await expect(page.locator('#apCount')).toHaveText('1 of 3');
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.__tones)).toBe(0);
});
