import { API, PANEL_HEADERS, curPage, h, haptic, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- app console
// Keys and a dial for the app in front on the PC. The agent says which profile matches the focused app (its label and
// key labels, never the app's name) and the phone sends back a profile id and a slot; the key combos stay on the PC.
// Dial turns are counted on the phone and sent as one request with a count, so a quick spin is a few requests at most.
const STEP_DEG = 24; // one dial click

async function post(path) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), 5000);
  try {
    const r = await fetch(API + path, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
    if (r.ok) return;
    let error = 'HTTP ' + r.status;
    try { error = (await r.json()).error || error; } catch (_) {}
    throw new Error(error);
  } finally { clearTimeout(timer); }
}

W['app-console'] = {
  needs: ['tick'],
  create(box, { o, page }) {
    box.classList.add('ac');
    const title = h('div', 'ac-title');
    const keys = h('div', 'ac-keys');
    const knob = h('div', 'ac-knob');
    const dial = h('div', 'ac-dial', knob);
    const note = h('div', 'empty ac-note', t('w.loading'));
    box.append(title, keys, dial, note);

    let alive = true, polling = false, lastPoll = 0, profile = null, sig = '';
    let angle = 0, turned = 0, moved = 0, pending = 0, sending = false;
    const fail = (e) => { toast(e?.message || t('w.offline'), 'err'); sig = ''; lastPoll = 0; };
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };

    const render = (state) => {
      profile = state && state.profile;
      const s = JSON.stringify(state || null);
      if (s === sig) return;
      sig = s;
      const usable = !!(profile && state.control);
      title.textContent = profile ? profile.label : '';
      keys.replaceChildren(...(profile ? profile.keys : []).map((label, i) => {
        const b = h('button', 'ac-key', label);
        b.disabled = !usable;
        b.onclick = () => { haptic(); post(`/console/${encodeURIComponent(profile.id)}/k${i}`).catch(fail); };
        return b;
      }));
      const d = profile && profile.dial;
      dial.hidden = !(usable && o.dial !== false && d && (d.left || d.right || d.press));
      box.classList.toggle('ac-has-dial', !dial.hidden);
      if (!state) say(t('w.offline'));
      else if (!state.supported) say(t('w.console.windowsOnly'));
      else if (!profile) say(t('w.console.noProfile'));
      else if (!state.control) say(t('w.console.controlOff'));
      else say('');
    };

    const poll = async () => {
      if (polling) return;
      polling = true; lastPoll = Date.now();
      try {
        const ctl = new AbortController();
        const timer = setTimeout(() => ctl.abort(), 4000);
        try {
          const r = await fetch(API + '/console', { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
          if (!alive) return;
          if (r.status === 404) { render({ supported: true, control: false, profile: null }); return; }
          render(r.ok ? await r.json() : null);
        } finally { clearTimeout(timer); }
      } catch (_) { if (alive) render(null); }
      finally { polling = false; }
    };

    // turns are sent one request at a time; what piles up meanwhile goes in the next one (at most 5 clicks each)
    const flush = () => {
      if (sending || !pending || !profile) return;
      const n = Math.max(-5, Math.min(5, pending));
      pending -= n;
      sending = true;
      post(`/console/${encodeURIComponent(profile.id)}/${n > 0 ? 'dial-right' : 'dial-left'}?times=${Math.abs(n)}`)
        .catch((e) => { pending = 0; fail(e); })
        .then(() => { sending = false; flush(); });
    };

    let start = null, onKnob = false;
    const at = (e) => { const r = dial.getBoundingClientRect(); return Math.atan2(e.clientY - (r.top + r.height / 2), e.clientX - (r.left + r.width / 2)) * 180 / Math.PI; };
    dial.onpointerdown = (e) => {
      const r = dial.getBoundingClientRect();
      onKnob = Math.hypot(e.clientX - (r.left + r.width / 2), e.clientY - (r.top + r.height / 2)) < r.width * 0.32;
      start = at(e); turned = 0; moved = 0; try { dial.setPointerCapture(e.pointerId); } catch (_) {} };
    dial.onpointermove = (e) => {
      if (start === null) return;
      const now = at(e);
      // near the centre a tiny jitter is a big angle: it neither turns nor spoils a press there
      const r = dial.getBoundingClientRect();
      if (Math.hypot(e.clientX - (r.left + r.width / 2), e.clientY - (r.top + r.height / 2)) < r.width * 0.2) { start = now; return; }
      let delta = now - start;
      if (delta > 180) delta -= 360; else if (delta < -180) delta += 360;
      start = now; angle += delta; turned += delta; moved += Math.abs(delta);
      knob.style.transform = `rotate(${angle}deg)`;
      while (Math.abs(turned) >= STEP_DEG) {
        const dir = turned > 0 ? 1 : -1;
        turned -= dir * STEP_DEG;
        if ((dir > 0 ? profile?.dial?.right : profile?.dial?.left)) { pending += dir; haptic(); }
      }
      flush();
    };
    dial.onpointerup = dial.onpointercancel = (e) => {
      // a press is a touch on the knob that hardly turned it
      const tap = start !== null && moved < 6 && e.type === 'pointerup' && onKnob;
      start = null;
      if (tap && profile?.dial?.press) { haptic(); post(`/console/${encodeURIComponent(profile.id)}/dial-press`).catch(fail); }
    };

    return {
      update() {
        // neighbouring pages get ticks too; the focused app is asked about only while this page is showing
        if (!alive || document.body.classList.contains('night') || pages[curPage] !== page) return;
        if (Date.now() - lastPoll > 1500) poll();
      },
      destroy() { alive = false; },
    };
  },
};
