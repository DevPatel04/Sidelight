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
