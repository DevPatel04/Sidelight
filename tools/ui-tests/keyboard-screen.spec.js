const { test, expect } = require('@playwright/test');

// Keyboard and Second screen widgets against the demo agent: which requests their taps make, and that both stay inert
// without their own switches.
async function layout(page, widgets) {
  await page.goto('/panel/');
  await page.evaluate((widgets) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets }] }));
  }, widgets);
  await page.reload();
  await page.evaluate(() => {
    window.__posts = [];
    const demo = window.fetch;
    window.fetch = (url, init) => {
      if (init && init.method === 'POST') window.__posts.push(decodeURIComponent(String(url).replace(/^.*\/agent\/api/, '')));
      return demo(url, init);
    };
  });
}
const keyboard = (options) => [{ id: 'kb', type: 'keyboard', x: 0, y: 0, w: 12, h: 6, options }];
const screen = (options) => [{ id: 'sc', type: 'screen', x: 0, y: 0, w: 12, h: 6, options }];
const key = (page, label) => page.locator('.kb-k', { hasText: new RegExp(`^${label.replace(/[\\^$.*+?()[\]{}|]/g, '\\$&')}$`) }).first();

test('keys go as text, and with a modifier as one combination, in the order tapped', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, keyboard({ control: true }));
  await key(page, 'h').dispatchEvent('pointerdown');
  await key(page, '⇧').dispatchEvent('pointerdown');
  await expect(key(page, 'A')).toBeVisible(); // Shift shows the capitals
  await key(page, '!').dispatchEvent('pointerdown'); // the 1 key, shifted
  await key(page, 'Ctrl').dispatchEvent('pointerdown');
  await key(page, 'c').dispatchEvent('pointerdown');
  await key(page, '⏎').dispatchEvent('pointerdown');
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual([
    '/keyboard?widget=test/kb&text=h', '/keyboard?widget=test/kb&text=!',
    '/keyboard?widget=test/kb&key=ctrl+c', '/keyboard?widget=test/kb&key=enter']);
  // a latched modifier is let go after one key
  await expect(key(page, 'Ctrl')).not.toHaveClass(/on/);
  expect(errors).toEqual([]);
});

test('a keyboard without its switch says so and sends nothing', async ({ page }) => {
  await layout(page, keyboard({}));
  await expect(page.locator('.kb-note')).toContainText('Type on the computer');
  await expect(key(page, 'q')).toBeDisabled();
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('the compact keyboard leaves out the F-keys and arrows', async ({ page }) => {
  await layout(page, keyboard({ control: true, layout: 'compact' }));
  await expect(key(page, 'q')).toBeVisible();
  await expect(page.locator('.kb-k', { hasText: /^F5$/ })).toHaveCount(0);
  await expect(page.locator('.kb-k', { hasText: /^←$/ })).toHaveCount(0);
});

test('the second screen shows the monitor and turns a tap into a click there', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, screen({ share: true, control: true }));
  const img = page.locator('.scr .cam-img.on');
  await expect(img).toBeVisible();
  await expect(page.locator('.scr .cam-note')).toBeHidden();
  const box = await page.locator('.scr-view').boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await expect.poll(() => page.evaluate(() => window.__posts.length)).toBe(1);
  const [post] = await page.evaluate(() => window.__posts);
  expect(post).toMatch(/^\/screen\/pointer\?widget=test\/sc&action=click&x=0\.5\d*&y=0\.5\d*$/);
  expect(errors).toEqual([]);
});

test('the second screen stays dark and ignores touches without its switches', async ({ page }) => {
  await layout(page, screen({}));
  await expect(page.locator('.scr .cam-note')).toContainText('Show the screen on the phone');
  await expect(page.locator('.scr .cam-img.on')).toHaveCount(0);
  const box = await page.locator('.scr-view').boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('the settings card installs the virtual display driver with one click and then offers to open it', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto('/settings/#sources');
  const status = page.locator('#screenStatus li');
  await expect(status).toHaveText(['Monitors found: 2', 'ffmpeg is installed', 'No virtual display driver yet']);
  await expect(page.locator('#screenOpen')).toBeHidden();
  await expect(page.locator('#screenInstallHint')).toBeVisible();
  await page.locator('#screenInstall').click();
  await expect(page.locator('#screenNote')).toHaveText('VDD Control is open: press Install there, then add a monitor');
  await expect(page.locator('#screenOpen')).toBeVisible();
  await expect(page.locator('#screenInstall')).toBeHidden();
  await expect(status.nth(2)).toHaveText('The virtual display driver is installed');
  expect(errors).toEqual([]);
});

test('on Wayland the settings card adds a virtual monitor of the chosen size and can forget the sharing answer', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.addInitScript(() => { window.__demoDisplay = 'wayland'; });
  await page.goto('/settings/#sources');
  const status = page.locator('#screenStatus li');
  await expect(status.nth(1)).toHaveText('The screen-sharing portal is available');
  await expect(status.nth(2)).toHaveText('GStreamer with its PipeWire plugin is installed');
  await expect(page.locator('#screenInstall')).toBeHidden();
  await expect(page.locator('#screenForget')).toBeHidden();
  await page.locator('#screenW').fill('2400');
  await page.locator('#screenH').fill('1080');
  await page.locator('#screenVirtual').click();
  await expect(page.locator('#screenNote')).toHaveText('Look at the computer: allow the screen-sharing dialog there');
  await expect(status.nth(3)).toHaveText('Virtual monitor: on, 2400 × 1080, while Sidelight runs');
  await expect(page.locator('#screenVirtual')).toHaveText('Remove the virtual monitor');
  await expect(page.locator('#screenSize')).toBeHidden();
  await expect(page.locator('#screenForget')).toHaveText('Choose the screen again');
  await page.locator('#screenVirtual').click();
  await expect(page.locator('#screenNote')).toHaveText('The virtual monitor is removed');
  await expect(page.locator('#screenVirtual')).toHaveText('Add a virtual monitor');
  expect(errors).toEqual([]);
});
