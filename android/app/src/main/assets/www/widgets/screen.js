import { API, PANEL_HEADERS, curPage, h, haptic, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- second screen
// A live picture of one of the PC's monitors (ScreenShare.cs), ideally a virtual one added for the phone, so it works
// as an extended screen: drag a window onto it on the PC and use it here. Pictures come one request at a time like the
// Camera widget's (two image layers, the next one decoded before it is swapped in), and only while the widget is really
// on screen. With "Click and drag on the screen" on, touches act on that monitor: tap to click, drag to move the
// pointer, hold then drag to drag, two fingers to scroll, a two-finger tap to right-click.
const DEFAULT_FPS = 8, MAX_FPS = 15; // ScreenShare.DefaultFps, MaxFps
const STATES = {
  unknown: 'w.screen.save', off: 'w.screen.off', unsupported: 'w.screen.unsupported', 'no-ffmpeg': 'w.screen.noFfmpeg',
  'no-monitor': 'w.screen.noMonitor', starting: 'w.screen.starting', failed: 'w.screen.failed', busy: 'w.screen.failed',
  // Wayland (WaylandScreen.cs): the sharing dialog is open on the PC, sharing was refused or stopped there, no GStreamer
  consent: 'w.screen.consent', denied: 'w.screen.denied', 'no-gstreamer': 'w.screen.noGst',
};
const HOLD_MS = 450, MOVE_PX = 10, SCROLL_PX = 24;

W.screen = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    box.classList.add('scr');
    const fit = o.fit === 'cover' ? 'cover' : 'contain';
    const layers = [h('img', 'cam-img'), h('img', 'cam-img')];
    for (const img of layers) { img.alt = ''; img.draggable = false; img.style.objectFit = fit; }
    const view = h('div', 'scr-view', ...layers);
    const note = h('div', 'empty cam-note', t(o.share === true ? 'w.screen.starting' : 'w.screen.off'));
    box.append(view, note);

    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const every = () => 1000 / Math.max(1, Math.min(MAX_FPS, Math.round(+o.fps) || DEFAULT_FPS));
    let alive = true, running = false, inflight = null, front = 0;
    const urls = [null, null];

    const visible = () => alive && box.isConnected && !document.hidden && !document.body.classList.contains('night')
      && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const clear = () => {
      for (let i = 0; i < 2; i++) {
        layers[i].classList.remove('on');
        layers[i].removeAttribute('src');
        if (urls[i]) { URL.revokeObjectURL(urls[i]); urls[i] = null; }
      }
    };
    const decoded = (img) => (img.decode ? img.decode() : new Promise((done, fail) => { img.onload = done; img.onerror = fail; }));

    const one = async () => {
      const ctl = new AbortController();
      inflight = ctl;
      let late;
      const deadline = new Promise((_, fail) => { late = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 8000); });
      deadline.catch(() => {});
      try {
        const fresh = urls[front] ? '' : '&fresh=true';
        const r = await Promise.race([fetch(`${API}/screen?${wq}${fresh}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }), deadline]);
        if (r.status === 204) return every();
        if (r.ok) {
          const url = URL.createObjectURL(await Promise.race([r.blob(), deadline]));
          const back = 1 - front, next = layers[back];
          next.src = url;
          try { await Promise.race([decoded(next), deadline]); } catch (_) {
            URL.revokeObjectURL(url);
            clear();
            say(t('w.screen.failed'));
            return every();
          }
          if (!visible()) { next.removeAttribute('src'); URL.revokeObjectURL(url); return 0; }
          if (urls[back]) URL.revokeObjectURL(urls[back]);
          urls[back] = url;
          next.classList.add('on');
          layers[front].classList.remove('on');
          front = back;
          say('');
          return every();
        }
        const body = await Promise.race([r.json().catch(() => ({})), deadline]);
        clear();
        say(t(STATES[body.state] || 'w.screen.failed'));
        return ['unknown', 'off', 'unsupported', 'no-monitor', 'denied'].includes(body.state) ? 5000 : 1500;
      } catch (_) {
        clear();
        say(t('w.offline'));
        return 3000;
      } finally { clearTimeout(late); inflight = null; }
    };

    const loop = async () => {
      if (running) return;
      running = true;
      try {
        while (visible()) {
          const started = Date.now();
          const wait = await one();
          if (!visible()) break;
          await new Promise((done) => setTimeout(done, Math.max(0, wait - (Date.now() - started))));
        }
      } finally {
        running = false;
        if (!visible()) { clear(); say(t(o.share === true ? 'w.screen.starting' : 'w.screen.off')); }
      }
    };

    // ---- touches → the PC's pointer, on the monitor shown
    const control = o.share === true && o.control === true;
    if (control) {
      view.classList.add('is-live');
      // pointer actions go one after another; a move waiting behind another move is replaced by the newer one
      const queue = [];
      let working = false, lastError = 0;
      const pump = async () => {
        if (working) return;
        working = true;
        while (queue.length && alive) {
          const { action, x, y, delta } = queue.shift();
          try {
            const q = `action=${action}&x=${x.toFixed(4)}&y=${y.toFixed(4)}` + (delta ? `&delta=${delta}` : '');
            const r = await fetch(`${API}/screen/pointer?${wq}&${q}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
            if (!r.ok) {
              let error = 'HTTP ' + r.status;
              try { error = (await r.json()).error || error; } catch (_) {}
              throw new Error(error);
            }
          } catch (e) {
            if (Date.now() - lastError > 4000) { lastError = Date.now(); toast(e?.message || t('w.offline'), 'err'); }
          }
        }
        working = false;
      };
      const act = (action, at, delta = 0) => {
        if (!at) return;
        const last = queue[queue.length - 1];
        if (action === 'move' && last?.action === 'move') queue.pop();
        queue.push({ action, x: at.x, y: at.y, delta });
        pump();
      };

      // where a touch is on the monitor, 0..1 each way, from where the picture really is inside the widget
      const at = (e) => {
        const img = layers[front];
        const rect = view.getBoundingClientRect();
        const iw = img.naturalWidth, ih = img.naturalHeight;
        if (!iw || !ih || !rect.width || !rect.height) return null;
        const scale = (fit === 'cover' ? Math.max : Math.min)(rect.width / iw, rect.height / ih);
        const dw = iw * scale, dh = ih * scale;
        const x = (e.clientX - rect.left - (rect.width - dw) / 2) / dw;
        const y = (e.clientY - rect.top - (rect.height - dh) / 2) / dh;
        return x < 0 || x > 1 || y < 0 || y > 1 ? null : { x, y };
      };

      const touches = new Map();
      let mode = '', start = null, hold = 0, scrollFrom = 0, scrolled = false, twoAt = 0, firstAt = null;
      const reset = () => { clearTimeout(hold); mode = ''; start = null; scrolled = false; firstAt = null; };
      view.addEventListener('pointerdown', (e) => {
        e.preventDefault(); e.stopPropagation();
        view.setPointerCapture?.(e.pointerId);
        touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (touches.size === 1) {
          start = { x: e.clientX, y: e.clientY };
          firstAt = at(e);
          mode = 'pending';
          hold = setTimeout(() => { if (mode === 'pending' && firstAt) { mode = 'drag'; haptic(); act('down', firstAt); } }, HOLD_MS);
        } else if (touches.size === 2 && mode !== 'drag') {
          clearTimeout(hold);
          mode = 'two'; twoAt = Date.now(); scrolled = false;
          scrollFrom = [...touches.values()].reduce((s, p) => s + p.y, 0) / 2;
        }
      });
      view.addEventListener('pointermove', (e) => {
        if (!touches.has(e.pointerId)) return;
        e.preventDefault();
        touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (mode === 'two' && touches.size === 2) {
          const y = [...touches.values()].reduce((s, p) => s + p.y, 0) / 2;
          const notches = Math.trunc((y - scrollFrom) / SCROLL_PX);
          if (notches) { scrolled = true; scrollFrom += notches * SCROLL_PX; act('scroll', firstAt || at(e), notches * 120); }
          return;
        }
        if (mode === 'pending' && Math.hypot(e.clientX - start.x, e.clientY - start.y) > MOVE_PX) { clearTimeout(hold); mode = 'hover'; }
        if (mode === 'hover' || mode === 'drag') act('move', at(e));
      });
      const end = (e) => {
        if (!touches.has(e.pointerId)) return;
        touches.delete(e.pointerId);
        const cancelled = e.type === 'pointercancel';
        if (mode === 'pending' && !cancelled) { haptic(); act('click', at(e) || firstAt); }
        else if (mode === 'drag') act('up', at(e) || firstAt);
        else if (mode === 'two' && !scrolled && !cancelled && Date.now() - twoAt < 400) { haptic(); act('rightclick', firstAt); mode = 'done'; }
        if (touches.size === 0) reset();
        else if (mode !== 'two') mode = 'done';
      };
      view.addEventListener('pointerup', end);
      view.addEventListener('pointercancel', end);
      view.addEventListener('contextmenu', (e) => e.preventDefault());
    }

    return {
      update() { if (visible()) loop(); },
      destroy() {
        alive = false;
        if (inflight) inflight.abort();
        clear();
      },
    };
  },
};
