import { API, PANEL_HEADERS, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- keyboard
// A full keyboard whose keys are pressed in the app in front on the PC (KeyboardInput.cs), only with this widget's own
// "Type on the computer" option on. A letter, digit or symbol goes as text, so the PC's own keyboard layout can't turn
// it into another character; a key with Ctrl / Alt / Win held, and the keys that aren't characters (Enter, arrows,
// F-keys…), go as one key combination. Modifiers latch for the next key; a double tap holds them until tapped again.
// Keys are sent one after another in the order tapped: a fast typist's keys never overtake each other on the way.

// shifted symbols of a US keyboard; letters just become capitals
const SHIFTED = { '`': '~', 1: '!', 2: '@', 3: '#', 4: '$', 5: '%', 6: '^', 7: '&', 8: '*', 9: '(', 0: ')', '-': '_', '=': '+',
  '[': '{', ']': '}', '\\': '|', ';': ':', "'": '"', ',': '<', '.': '>', '/': '?' };
// the key names KeySender knows for the symbols, for combinations such as Ctrl+/ or Ctrl+-
const KEY_NAME = { '`': 'backquote', '-': 'minus', '=': 'equals', '[': 'bracketleft', ']': 'bracketright', '\\': 'backslash',
  ';': 'semicolon', "'": 'quote', ',': 'comma', '.': 'period', '/': 'slash' };
const MODS = ['ctrl', 'shift', 'alt', 'win'];
// held down, these repeat like a real keyboard's
const REPEATS = new Set(['backspace', 'delete', 'left', 'right', 'up', 'down', 'space']);

const k = (code, label, w = 1) => ({ code, label, w });
const chars = (s) => [...s].map((c) => k(c, c));
const FKEYS = [k('esc', 'Esc', 1.25), ...Array.from({ length: 12 }, (_, i) => k('f' + (i + 1), 'F' + (i + 1))), k('delete', 'Del', 1.25)];
const ROWS = (full) => [
  ...(full ? [FKEYS] : []),
  [...chars('`1234567890-='), k('backspace', '⌫', 1.6)],
  [k('tab', 'Tab', 1.4), ...chars('qwertyuiop[]'), k('\\', '\\', 1.2)],
  [k('caps', 'Caps', 1.7), ...chars("asdfghjkl;'"), k('enter', '⏎', 1.9)],
  [k('shift', '⇧', 2.2), ...chars('zxcvbnm,./'), ...(full ? [k('up', '↑', 1.1)] : []), k('shift', '⇧', full ? 1.3 : 2.4)],
  [k('ctrl', 'Ctrl', 1.3), k('win', 'Win', 1.3), k('alt', 'Alt', 1.3), k('space', '', 6.5),
    ...(full ? [k('left', '←', 1.1), k('down', '↓', 1.1), k('right', '→', 1.1)] : [k('enter', '⏎', 1.5)])],
];

W.keyboard = {
  needs: [],
  create(box, { o, page, w }) {
    box.classList.add('kb-w');
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const on = o.control === true;
    const feel = o.haptics !== false;
    const board = h('div', 'kb' + (on ? '' : ' is-off'));
    box.append(board);
    if (!on) box.append(h('div', 'kb-note', t('w.kb.controlOff')));

    // modifier state: 0 off, 1 for the next key, 2 held until tapped again
    const mods = { ctrl: 0, shift: 0, alt: 0, win: 0 };
    let caps = false, alive = true, chain = Promise.resolve(), lastError = 0;
    const modKeys = [], charKeys = [];

    const post = async (query) => {
      try {
        const r = await fetch(`${API}/keyboard?${wq}&${query}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) {
          let error = 'HTTP ' + r.status;
          try { error = (await r.json()).error || error; } catch (_) {}
          throw new Error(error);
        }
      } catch (e) {
        // one message for a burst of failed keys, not one per key
        if (alive && Date.now() - lastError > 4000) { lastError = Date.now(); toast(e?.message || t('w.offline'), 'err'); }
      }
    };
    const send = (query) => { chain = chain.then(() => post(query)); };

    const paint = () => {
      for (const [el, m] of modKeys) { el.classList.toggle('on', mods[m] > 0); el.classList.toggle('lock', mods[m] === 2); }
      const upper = (mods.shift > 0) !== caps;
      for (const [el, c] of charKeys) {
        const shifted = mods.shift > 0 ? SHIFTED[c] : null;
        el.textContent = /[a-z]/.test(c) ? (upper ? c.toUpperCase() : c) : (shifted || c);
      }
      capsKey?.classList.toggle('on', caps);
    };
    const releaseLatched = () => { for (const m of MODS) if (mods[m] === 1) mods[m] = 0; paint(); };
    const held = () => MODS.filter((m) => mods[m] > 0);

    const press = (code) => {
      if (code === 'caps') { caps = !caps; paint(); return; }
      const isChar = code.length === 1;
      const combo = held().filter((m) => m !== 'shift');
      if (isChar && combo.length === 0) {
        // plain typing: the character itself, as the phone shows it
        const shifted = mods.shift > 0;
        const text = /[a-z]/.test(code) ? (shifted !== caps ? code.toUpperCase() : code) : (shifted ? SHIFTED[code] || code : code);
        send('text=' + encodeURIComponent(text));
      } else {
        const name = isChar ? KEY_NAME[code] || code : code;
        send('key=' + encodeURIComponent([...held(), name].join('+')));
      }
      releaseLatched();
    };

    const lastTap = {};
    const toggleMod = (m) => {
      const now = Date.now();
      // a quick second tap holds the modifier; tapping a held one lets it go
      if (mods[m] === 2) mods[m] = 0;
      else if (mods[m] === 1 && now - (lastTap[m] || 0) < 400) mods[m] = 2;
      else mods[m] = mods[m] ? 0 : 1;
      lastTap[m] = now;
      paint();
    };

    let capsKey = null;
    for (const row of ROWS(o.layout !== 'compact')) {
      const r = h('div', 'kb-row');
      for (const key of row) {
        const b = h('button', 'kb-k', key.label);
        b.type = 'button';
        b.style.flexGrow = String(key.w);
        b.setAttribute('aria-label', key.code === 'space' ? 'Space' : key.label);
        if (MODS.includes(key.code)) { b.classList.add('kb-mod'); modKeys.push([b, key.code]); }
        else if (key.code.length === 1) charKeys.push([b, key.code]);
        if (key.code === 'caps') { b.classList.add('kb-mod'); capsKey = b; }
        if (key.code === 'space') b.classList.add('kb-space');
        if (on) {
          let repeat = 0, delay = 0;
          const stop = () => { clearTimeout(delay); clearInterval(repeat); delay = repeat = 0; b.classList.remove('down'); };
          b.addEventListener('pointerdown', (e) => {
            e.preventDefault(); e.stopPropagation();
            b.classList.add('down');
            if (feel) haptic();
            if (MODS.includes(key.code)) { toggleMod(key.code); return; }
            press(key.code);
            if (REPEATS.has(key.code)) delay = setTimeout(() => { repeat = setInterval(() => press(key.code), 70); }, 450);
          });
          ['pointerup', 'pointercancel', 'pointerleave'].forEach((ev) => b.addEventListener(ev, stop));
        } else b.disabled = true;
        r.append(b);
      }
      board.append(r);
    }
    paint();

    return {
      update() {},
      destroy() { alive = false; },
    };
  },
};
