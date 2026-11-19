// The Glass theme and the page backgrounds (Settings › Display), against the web demo.
const { test, expect } = require('@playwright/test');

async function show(page, display) {
  await page.goto('/panel/');
  await page.evaluate((display) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, ...display },
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'st', type: 'stat', x: 0, y: 0, w: 6, h: 3, options: { metric: 'cpu' } },
        { id: 'nt', type: 'note', x: 6, y: 0, w: 6, h: 3, options: { text: 'Glass sample' } },
      ] }],
    }));
  }, display);
  await page.reload();
  await expect(page.locator('.w-note').first()).toBeVisible();
}

test('glass gives the widgets its own colours and drifting colour behind them', async ({ page }) => {
  await show(page, { theme: 'glass' });
  await expect(page.locator('body')).toHaveAttribute('data-theme', 'glass');
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'blobs');
  // widgets read their colour from the root when they are built, so the theme has to be there first
  await expect.poll(() => page.locator('.w-stat').evaluate((n) => n.style.getPropertyValue('--c'))).toBe('#5ef0ff');
  await expect(page.locator('#bgFx')).toBeVisible();
  // a frosted card, not the default flat one
  const bg = await page.locator('.w-stat').evaluate((n) => getComputedStyle(n).backgroundImage);
  expect(bg).toContain('rgba(255, 255, 255, 0.19)');
});

test('the other themes keep the accent glow and their colours', async ({ page }) => {
  await show(page, { theme: 'glass' }); // switching away rebuilds the widgets in the new colours
  await show(page, {});
  await expect(page.locator('body')).toHaveAttribute('data-theme', 'default');
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'glow');
  await expect(page.locator('#bgFx')).toBeHidden();
  await expect.poll(() => page.locator('.w-stat').evaluate((n) => n.style.getPropertyValue('--c'))).toBe('#2dd4bf');
});

test('a chosen background replaces the theme\'s own, and an unknown one falls back', async ({ page }) => {
  await show(page, { theme: 'midnight', background: 'rain' });
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'rain');
  await expect(page.locator('#bgFx')).toBeVisible();
  const glow = await page.evaluate(() => getComputedStyle(document.body, '::before').display);
  expect(glow).toBe('none');
  await show(page, { theme: 'glass', background: 'fireworks' });
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'blobs');
});

test('a picture id that is not one is never requested', async ({ page }) => {
  const asked = [];
  page.on('request', (r) => { if (r.url().includes('/config/background')) asked.push(r.url()); });
  await show(page, { theme: 'glass', backgroundImage: '../../settings' });
  await expect(page.locator('body')).not.toHaveClass(/has-bg-image/);
  expect(asked).toEqual([]);
});

test('cockpit draws the clock on an LCD and gauges as needle dials over the view ahead', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, theme: 'cockpit' },
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'ck', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: {} },
        { id: 'ga', type: 'gauge', x: 6, y: 0, w: 3, h: 3, options: { metric: 'cpu' } },
        { id: 'st', type: 'stat', x: 9, y: 0, w: 3, h: 3, options: { metric: 'ram' } },
      ] }],
    }));
  });
  await page.reload();
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'horizon');
  // four digits, and the time is still there for a screen reader
  await expect(page.locator('.clock.lcd .lcd7')).toHaveCount(4);
  await expect(page.locator('.clock.lcd')).toHaveAttribute('aria-label', /^\d{1,2}:\d{2}$/);
  await expect(page.locator('.w-gauge .needle-dial .needle')).toHaveCount(1);
  await expect(page.locator('.w-stat .t-dial .needle')).toHaveCount(1);
  // the needle turns away from zero once a reading arrives
  await expect.poll(() => page.locator('.w-gauge .needle').evaluate((n) => n.style.transform)).not.toBe('rotate(135.0deg)');
});

test('nature grows the clock from leaves and shows the load as a tree, a campfire and a pond', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, theme: 'nature' },
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'ck', type: 'clock', x: 0, y: 0, w: 6, h: 3, options: {} },
        { id: 'cpu', type: 'gauge', x: 6, y: 0, w: 2, h: 3, options: { metric: 'cpu' } },
        { id: 'gpu', type: 'gauge', x: 8, y: 0, w: 2, h: 3, options: { metric: 'gpu' } },
        { id: 'ram', type: 'stat', x: 10, y: 0, w: 2, h: 3, options: { metric: 'ram' } },
      ] }],
    }));
  });
  await page.reload();
  await expect(page.locator('body')).toHaveAttribute('data-bg', 'forest');
  await expect(page.locator('.clock.leafy .leaf7')).toHaveCount(4);
  await expect(page.locator('.clock.leafy .berries')).toHaveCount(1);
  await expect(page.locator('.clock.leafy')).toHaveAttribute('aria-label', /^\d{1,2}:\d{2}$/);
  // the canopy fills once a reading arrives, the fire and the pond take theirs
  await expect.poll(() => page.locator('.w-gauge .g-art .tree .cn:not(.off)').count()).toBeGreaterThan(0);
  await expect(page.locator('.w-gauge .g-art .fire')).toHaveCount(1);
  await expect.poll(() => page.locator('.w-stat .t-art .water').evaluate((n) => n.style.transform)).not.toBe('translateY(54px)');
});
