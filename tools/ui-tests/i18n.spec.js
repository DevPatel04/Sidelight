// Localisation gaps the first i18n pass left behind: literals the dictionary already translates, and one spot
// that compared a localized label against the English word. Against the web demo, in German.
const { test, expect } = require('@playwright/test');

async function show(page, widgets) {
  await page.goto('/panel/');
  await page.evaluate((widgets) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'de' },
      pages: [{ id: 'one', title: 'One', widgets }],
    }));
  }, widgets);
  await page.reload();
}

test('a small agenda shows relative times in the panel language', async ({ page }) => {
  await show(page, [{ id: 'ag', type: 'agenda', x: 0, y: 0, w: 12, h: 1, options: {} }]);
  await page.evaluate(() => {
    const realFetch = window.fetch;
    const t = Date.now();
    // only a timed event still ahead today, so the compact agenda takes the "in N min" branch
    window.fetch = (input, init) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/api/calendar')) {
        return Promise.resolve(new Response(JSON.stringify({
          updated: t, errors: [],
          events: [{ title: 'Review', start: t + 35 * 60e3, end: t + 65 * 60e3, allDay: false, calendar: 'Work', color: '#60a5fa' }],
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      return realFetch(input, init);
    };
    window.PhoneBoxDemo.emit('refresh');
  });
  const sub = page.locator('.ag-big-s').first();
  await expect(sub).toHaveText(/^in \d+ Min/); // German "in 35 Min · 14:30", not "Heute · 14:30"
  await expect(sub).not.toHaveText(/Heute/);
});

test('the breathe widget greets in the panel language before the first tap', async ({ page }) => {
  await show(page, [{ id: 'br', type: 'breathe', x: 0, y: 0, w: 4, h: 2, options: {} }]);
  await expect(page.locator('.br-w')).toHaveText('Tippen zum Starten');
});
