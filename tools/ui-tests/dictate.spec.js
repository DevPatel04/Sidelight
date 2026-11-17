const { test, expect } = require('@playwright/test');

// Dictation: the Android bridge recognizes speech (faked here), the widget sends the text to the agent (the demo).
// The fake has to be there before main.js builds the bridge, so it comes from an init script.
async function layout(page, options, { app = true } = {}) {
  if (app) {
    await page.addInitScript(() => {
      window.__listen = [];
      window.Android = {
        canListen: () => true,
        listen: (key, lang, offline) => {
          window.__listen.push({ lang, offline });
          setTimeout(() => window.onAndroidSpeech('ready', ''), 10);
          setTimeout(() => window.onAndroidSpeech('partial', 'hello'), 30);
          setTimeout(() => window.onAndroidSpeech('final', 'hello world'), 60);
        },
        stopListening: () => {},
        cancelListening: () => { window.__cancelled = (window.__cancelled || 0) + 1; },
      };
    });
  }
  await page.goto('/panel/');
  await page.evaluate((options) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'dc', type: 'dictate', x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, options);
  await page.reload();
  await expect(page.locator('.w.dc')).toBeVisible();
  await page.evaluate(() => {
    window.__posts = [];
    const demo = window.fetch;
    window.fetch = (url, init) => {
      if (init && init.method === 'POST' && String(url).includes('/dictate')) window.__posts.push([String(url).replace(/^.*\/agent\/api/, ''), init.body]);
      return demo(url, init);
    };
  });
}

test('dictation sends what the phone heard for its own widget', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, { control: true, language: 'tr-TR' });
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-text')).toHaveText('hello world');
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual([['/dictate?widget=test%2Fdc', JSON.stringify({ text: 'hello world' })]]);
  await expect(page.locator('.dc-note')).toHaveText('Typed on the computer');
  expect(await page.evaluate(() => window.__listen)).toEqual([{ lang: 'tr-TR', offline: false }]);
  expect(errors).toEqual([]);
});

test('dictation without its switch keeps the text on the phone', async ({ page }) => {
  await layout(page, {});
  await expect(page.locator('.dc-note')).toContainText('Type the text on the computer');
  await page.locator('.dc-mic').click();
  await expect(page.locator('.dc-text')).toHaveText('hello world');
  expect(await page.evaluate(() => window.__posts)).toEqual([]);
});

test('dictation in a browser or an older app says to update', async ({ page }) => {
  await layout(page, { control: true }, { app: false });
  await expect(page.locator('.dc-note')).toHaveText('Update the PhoneBox app to dictate');
});
