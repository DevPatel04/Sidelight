const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

async function openPlugins(page) {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#plugins');
  await expect(page.locator('section.on h2')).toHaveText('Plugins');
}

async function installDemoPluginAndEnable(page) {
  await page.locator('#plLocalPath').fill('./demo-card');
  await page.getByRole('button', { name: 'Install from path' }).click();
  const item = page.locator('#plInstalled .pl-item', { hasText: 'Demo Card' });
  await expect(item).toBeVisible();
  await expect(item.locator('.pl-tag', { hasText: 'Disabled' })).toBeVisible();
  await expect(item.locator('.switch input')).not.toBeChecked();

  await item.locator('.switch').click();
  await expect(item.locator('.switch input')).toBeChecked();
  await expect(item.locator('.pl-tag', { hasText: 'Active' })).toBeVisible();
  return item;
}

test.beforeEach(async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => { try { localStorage.clear(); } catch (_) {} });
});

test('local plugin starts disabled, renders declarative card on panel after enabling, and preserves secrets', async ({ page }) => {
  const errors = watchErrors(page);
  await openPlugins(page);
  const item = await installDemoPluginAndEnable(page);

  await page.locator('#nav a', { hasText: 'Pages' }).click();
  await expect(page.locator('section.on h2')).toHaveText('Pages');
  await page.locator('#pageTabs .ptab').first().click();
  await page.locator('#peHead').getByRole('button', { name: 'Clear', exact: true }).click();
  const pal = page.locator('#palette .pal', { hasText: 'Demo Card' });
  await expect(pal).toBeVisible();
  await pal.click();
  await page.locator('#save').click();
  await expect(page.locator('#toast')).toContainText('Saved');

  await page.goto('/panel/');
  const widget = page.locator('.w-plugin').first();
  await expect(widget).toBeVisible();
  await expect(widget).toContainText('Ready');

  await openPlugins(page);
  await item.getByRole('button', { name: 'Settings', exact: true }).click();
  const modal = page.locator('#modalBody');
  await expect(modal).toBeVisible();
  await modal.locator('input[type="text"]').fill('Focus');
  await modal.locator('input[type="password"]').fill('super-secret-token');
  await modal.getByRole('button', { name: 'Save plugin settings' }).click();
  await expect(page.locator('#modal')).toBeHidden();

  let overview = await page.evaluate(async () => (await fetch('/api/plugins')).json());
  let card = overview.installed.find((x) => x.id === 'demo-card');
  expect(card.secretKeysSet).toContain('token');
  expect(card.settings.label).toBe('Focus');
  expect(card.settings.token).toBeUndefined();

  await item.getByRole('button', { name: 'Settings', exact: true }).click();
  const pw = modal.locator('input[type="password"]');
  await expect(pw).toHaveAttribute('placeholder', 'Saved — leave blank to keep, or enter new');
  await expect(modal.getByRole('button', { name: 'Clear', exact: true })).toBeVisible();
  await modal.locator('input[type="text"]').fill('Focus 2');
  await modal.getByRole('button', { name: 'Save plugin settings' }).click();
  await expect(page.locator('#modal')).toBeHidden();

  overview = await page.evaluate(async () => (await fetch('/api/plugins')).json());
  card = overview.installed.find((x) => x.id === 'demo-card');
  expect(card.secretKeysSet).toContain('token');
  expect(card.settings.label).toBe('Focus 2');
  expect(card.settings.token).toBeUndefined();

  page.once('dialog', (d) => d.accept());
  await item.getByRole('button', { name: 'Uninstall', exact: true }).click();
  await expect(page.locator('#plInstalled .pl-item')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('missing plugin widget shows clean empty state without console errors', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/panel/');
  await page.evaluate(() => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    const pages = [{ id: 'p1', title: 'P1', icon: '🧩', enabled: true,
      widgets: [{ id: 'pl1', type: 'plugin:missing-plug', x: 0, y: 0, w: 4, h: 2, options: {} }] }];
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, shortcuts: cfg.shortcuts || [] }));
  });
  await page.reload();
  await expect(page.locator('.w-plugin .empty')).toContainText('Plugin not installed');
  expect(errors).toEqual([]);
});
