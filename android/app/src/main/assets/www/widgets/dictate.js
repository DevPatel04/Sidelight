import { API, PANEL_HEADERS, bridge, curPage, h, haptic, icon, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- dictation
// Tap the microphone, speak, and the text is typed into the app in front on the PC. The phone's own speech recognizer
// (Android, through the app's bridge) turns speech into text; the agent types it, and only when this widget's own
// "Type the text on the computer" option is on. The microphone is open only while the button shows it, at most a
// minute, and never while the widget is out of sight. Nothing is stored.

// Each recognition session has an id from the bridge; its events go to the widget that started it, so one widget's
// session never ends or answers another's.
const sessions = new Map();
window.onAndroidSpeech = (kind, text, id) => { const handler = sessions.get(id); if (handler) handler(kind, text); };

const ERRORS = {
  '1': 'w.dictate.network', '2': 'w.dictate.network', '4': 'w.dictate.network', '13': 'w.dictate.network',
  '6': 'w.dictate.noMatch', '7': 'w.dictate.noMatch',
  '9': 'w.dictate.permission', permission: 'w.dictate.permission', 'permission-blocked': 'w.dictate.permissionBlocked',
  unavailable: 'w.dictate.unavailable', 'offline-unsupported': 'w.dictate.offlineUnsupported',
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

    let alive = true, session = 0;
    const can = !!(bridge && bridge.listen && bridge.canListen);
    const visible = () => !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const done = () => { sessions.delete(session); session = 0; mic.classList.remove('on'); };
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
        done();
        said.textContent = text;
        if (text.trim()) send(text); else say('w.dictate.noMatch');
      } else if (kind === 'error') {
        done();
        say(text === 'cancelled' ? 'w.dictate.tap' : ERRORS[text] || 'w.dictate.noMatch');
      }
    }

    mic.onclick = () => {
      haptic();
      if (!can) { say('w.dictate.update'); return; }
      if (session) { bridge.stopListening(session); return; }
      if (!bridge.canListen()) { say('w.dictate.unavailable'); return; }
      // a session another widget runs is cancelled by the bridge, and that widget hears so through its own id
      session = bridge.listen(String(o.language || '').trim(), o.offline === true) || 0;
      if (!session) return;
      sessions.set(session, onSpeech);
      mic.classList.add('on');
      said.textContent = '';
      say('w.dictate.listening');
    };

    if (!can) say('w.dictate.update');
    else if (o.control !== true) say('w.dictate.controlOff');

    return {
      update() {
        // the microphone closes when the widget leaves the screen (another page or card, the night screen)
        if (session && !visible()) { bridge.cancelListening(session); done(); say('w.dictate.tap'); }
      },
      destroy() {
        alive = false;
        if (session) bridge.cancelListening(session);
        done();
      },
    };
  },
};
