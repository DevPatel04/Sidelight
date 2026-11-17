import { API, PANEL_HEADERS, bridge, curPage, h, haptic, icon, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- dictation
// Tap the microphone, speak, and the text is typed into the app in front on the PC. The phone's own speech recognizer
// (Android, through the app's bridge) turns speech into text; the agent types it, and only when this widget's own
// "Type the text on the computer" option is on. The microphone is open only while the button shows it, at most a
// minute, and never while the widget is out of sight. Nothing is stored.

// The bridge has one recognizer; the widget that started it gets its results.
let listener = null;
window.onAndroidSpeech = (kind, text) => { if (listener) listener(kind, text); };

const ERRORS = {
  '1': 'w.dictate.network', '2': 'w.dictate.network', '4': 'w.dictate.network', '13': 'w.dictate.network',
  '6': 'w.dictate.noMatch', '7': 'w.dictate.noMatch',
  '9': 'w.dictate.permission', permission: 'w.dictate.permission', 'permission-blocked': 'w.dictate.permissionBlocked',
  unavailable: 'w.dictate.unavailable',
};

W.dictate = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('dc');
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const mic = h('button', 'dc-mic', icon('mic'));
    mic.setAttribute('aria-label', t('w.dictate.tap'));
    const said = h('div', 'dc-text');
    const note = h('div', 'dc-note', t('w.dictate.tap'));
    box.append(mic, said, note);

    let alive = true, listening = false;
    const can = !!(bridge && bridge.listen && bridge.canListen);
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const set = (on) => { listening = on; mic.classList.toggle('on', on); if (!on && listener === onSpeech) listener = null; };
    const say = (key) => { note.textContent = key ? t(key) : ''; };

    async function send(text) {
      if (o.control !== true) { say('w.dictate.controlOff'); return; }
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 10_000);
      try {
        const r = await fetch(`${API}/dictate?${wq}`, { method: 'POST', cache: 'no-store', signal: ctl.signal,
          headers: { ...PANEL_HEADERS, 'Content-Type': 'application/json' }, body: JSON.stringify({ text }) });
        if (!r.ok) {
          let error = 'HTTP ' + r.status;
          try { error = (await r.json()).error || error; } catch (_) {}
          throw new Error(error);
        }
        if (alive) say('w.dictate.sent');
      } catch (e) {
        if (alive) { say(''); toast(e?.message || t('w.offline'), 'err'); }
      } finally { clearTimeout(timer); }
    }

    function onSpeech(kind, text) {
      if (!alive) return;
      if (kind === 'ready') { said.textContent = ''; say('w.dictate.listening'); }
      else if (kind === 'partial') said.textContent = text;
      else if (kind === 'final') {
        set(false);
        said.textContent = text;
        if (text.trim()) send(text); else say('w.dictate.noMatch');
      } else if (kind === 'error') {
        set(false);
        say(text === 'cancelled' ? 'w.dictate.tap' : ERRORS[text] || 'w.dictate.noMatch');
      }
    }

    mic.onclick = () => {
      haptic();
      if (!can) { say('w.dictate.update'); return; }
      if (listening) { bridge.stopListening(); return; }
      if (!bridge.canListen()) { say('w.dictate.unavailable'); return; }
      if (listener && listener !== onSpeech) bridge.cancelListening(); // another dictation widget had it
      listener = onSpeech;
      set(true);
      said.textContent = '';
      say('w.dictate.listening');
      bridge.listen(String(o.language || '').trim(), o.offline === true);
    };

    if (!can) say('w.dictate.update');
    else if (o.control !== true) say('w.dictate.controlOff');

    return {
      update() {
        // the microphone closes when the widget leaves the screen (another page or card, the night screen)
        if (listening && !visible()) { bridge.cancelListening(); set(false); say('w.dictate.tap'); }
      },
      destroy() {
        alive = false;
        if (listening) bridge.cancelListening();
        set(false);
      },
    };
  },
};
