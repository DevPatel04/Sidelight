import { API, PANEL_HEADERS } from './main.js';

// ---------------------------------------------------------------- page background (Settings › Display)
// A canvas behind the pages draws the chosen animation, and an optional picture of the user's own sits under it.
// Old phones run this for hours, so the canvas is drawn at a fraction of the screen's pixels (the soft shapes do not
// need more), at a capped frame rate, and not at all on the night screen, in a hidden WebView, or when the system
// asks for reduced motion (then one still frame is drawn).
const MODES = ['glow', 'blobs', 'aurora', 'stars', 'bubbles', 'rain', 'horizon', 'none'];
// the drawing scale against CSS pixels and frames per second: blurred shapes are fine small and slow
const SPEC = {
  blobs: { scale: 0.2, fps: 20 }, aurora: { scale: 0.25, fps: 20 },
  stars: { scale: 1, fps: 24 }, bubbles: { scale: 0.75, fps: 30 }, rain: { scale: 0.75, fps: 30 },
  horizon: { scale: 0.5, fps: 20 },
};
// the colour sets the animations draw in; glass brings its own, the other themes follow the accent colour
const GLASS = ['255,95,162', '124,92,255', '36,212,255', '255,179,71', '46,230,166'];

let canvas = null, ctx = null, imgLayer = null;
let mode = 'glow', palette = GLASS, raf = 0, last = 0, w = 0, hgt = 0, seeds = null;
let imageId = '', imageUrl = '';
const reduced = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;

function layers() {
  if (canvas) return;
  imgLayer = document.createElement('div');
  imgLayer.id = 'bgImage';
  canvas = document.createElement('canvas');
  canvas.id = 'bgFx';
  canvas.setAttribute('aria-hidden', 'true');
  const pager = document.getElementById('pager');
  document.body.insertBefore(imgLayer, pager);
  document.body.insertBefore(canvas, pager);
  ctx = canvas.getContext('2d');
  window.addEventListener('resize', () => { resize(); still(); });
  document.addEventListener('visibilitychange', () => (document.hidden ? stop() : start()));
  reduced?.addEventListener?.('change', () => { stop(); start(); });
  // the night screen covers everything: stop drawing under it
  new MutationObserver(() => (document.body.classList.contains('night') ? stop() : start()))
    .observe(document.body, { attributes: true, attributeFilter: ['class'] });
}

function resize() {
  const s = SPEC[mode];
  if (!s) return;
  w = Math.max(1, Math.round(innerWidth * s.scale));
  hgt = Math.max(1, Math.round(innerHeight * s.scale));
  if (canvas.width !== w || canvas.height !== hgt) { canvas.width = w; canvas.height = hgt; }
}

// fixed pseudo-random positions, so a redraw after a resize or a theme change keeps the same sky
function seeded(n) {
  let x = 1234567;
  const r = () => ((x = (x * 16807) % 2147483647) / 2147483647);
  return Array.from({ length: n }, () => [r(), r(), r(), r()]);
}

const DRAW = {
  // large soft colour blobs drifting slowly, what frosted glass looks best over
  blobs(t) {
    ctx.clearRect(0, 0, w, hgt);
    const m = Math.max(w, hgt);
    palette.forEach((col, i) => {
      const [bx, by] = [[.2, .3], [.77, .22], [.54, .83], [.1, .92], [.92, .83]][i % 5];
      const x = (bx + Math.sin(t * 0.00021 * (1 + i * 0.3) + i) * 0.12) * w;
      const y = (by + Math.cos(t * 0.00017 * (1 + i * 0.2) + i * 2) * 0.17) * hgt;
      const r = m * (0.27 + (i % 3) * 0.03);
      const g = ctx.createRadialGradient(x, y, 0, x, y, r);
      g.addColorStop(0, `rgba(${col},.8)`);
      g.addColorStop(1, `rgba(${col},0)`);
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, w, hgt);
    });
  },
  // curtains of light waving across the top of the screen
  aurora(t) {
    ctx.clearRect(0, 0, w, hgt);
    ctx.globalCompositeOperation = 'lighter';
    for (let b = 0; b < 3; b++) {
      const col = palette[(b * 2) % palette.length];
      for (let x = 0; x <= w; x += 2) {
        const k = x / w;
        const y = hgt * (0.28 + b * 0.08) + Math.sin(k * 5 + t * 0.0004 * (1 + b * 0.4) + b) * hgt * 0.08
          + Math.sin(k * 13 - t * 0.0007) * hgt * 0.025;
        const len = hgt * (0.2 + 0.12 * Math.sin(k * 7 + t * 0.0005 + b * 2));
        const g = ctx.createLinearGradient(0, y - len, 0, y);
        g.addColorStop(0, `rgba(${col},0)`);
        g.addColorStop(1, `rgba(${col},${0.22 + 0.12 * Math.sin(k * 9 + t * 0.001 + b)})`);
        ctx.fillStyle = g;
        ctx.fillRect(x, y - len, 2, len + hgt * 0.02);
      }
    }
    ctx.globalCompositeOperation = 'source-over';
  },
  // a still sky with twinkling stars and now and then a shooting star
  stars(t) {
    ctx.clearRect(0, 0, w, hgt);
    seeds ||= seeded(160);
    for (const [sx, sy, sz, ph] of seeds) {
      const a = 0.35 + 0.65 * Math.abs(Math.sin(t * 0.0006 * (0.5 + ph) + ph * 40));
      ctx.fillStyle = `rgba(255,255,255,${(a * (0.55 + sz * 0.45)).toFixed(2)})`;
      const s = sz > 0.92 ? 2.6 : sz > 0.6 ? 1.8 : 1.2;
      ctx.fillRect(((sx * w + t * 0.002 * sz) % w), sy * hgt, s, s);
    }
    const p = (t % 9000) / 1100; // one shooting star every 9 s, for about a second
    if (p < 1) {
      const x0 = w * 0.75 - p * w * 0.35, y0 = hgt * 0.12 + p * hgt * 0.2;
      const g = ctx.createLinearGradient(x0, y0, x0 + 70, y0 - 40);
      g.addColorStop(0, `rgba(255,255,255,${(0.9 * (1 - p)).toFixed(2)})`);
      g.addColorStop(1, 'rgba(255,255,255,0)');
      ctx.strokeStyle = g; ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x0 + 70, y0 - 40); ctx.stroke();
    }
  },
  // bubbles rising and wobbling, in the theme's colours
  bubbles(t) {
    ctx.clearRect(0, 0, w, hgt);
    seeds ||= seeded(38);
    seeds.forEach(([sx, sy, sz, ph], i) => {
      const r = (2 + sz * 9) * (w / 600);
      const y = hgt + r - ((t * (0.012 + sz * 0.02) * (hgt / 360) + sy * hgt * 1.4) % (hgt + r * 2 + 40));
      const x = sx * w + Math.sin(t * 0.0012 + ph * 9) * r * 1.2;
      const col = palette[i % palette.length];
      const g = ctx.createRadialGradient(x - r * 0.35, y - r * 0.35, r * 0.1, x, y, r);
      g.addColorStop(0, 'rgba(255,255,255,.2)');
      g.addColorStop(0.7, `rgba(${col},.05)`);
      g.addColorStop(1, `rgba(${col},.22)`);
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.arc(x, y, r, 0, 7); ctx.fill();
    });
  },
  // slanted rain, faster and brighter drops in front
  rain(t) {
    ctx.clearRect(0, 0, w, hgt);
    seeds ||= seeded(140);
    ctx.lineWidth = Math.max(1, w / 900);
    ctx.lineCap = 'round';
    for (const [sx, sy, sz] of seeds) {
      const sp = (0.35 + sz * 0.5) * (hgt / 360);
      const len = (7 + sz * 12) * (hgt / 360);
      const y = ((sy * (hgt + 40) + t * sp) % (hgt + 40)) - 20;
      const x = ((sx * w - (t * sp * 0.18)) % w + w) % w;
      ctx.strokeStyle = `rgba(200,215,255,${(0.12 + sz * 0.3).toFixed(2)})`;
      ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x - len * 0.22, y + len); ctx.stroke();
    }
  },
};

// the view through a windscreen at dusk: a low sun, clouds, the ground streaming towards you, a gentle bank
DRAW.horizon = (t) => {
  seeds ||= seeded(7);
  const bank = Math.sin(t * 0.00018) * 0.035, horizon = hgt * 0.6;
  ctx.save();
  ctx.translate(w / 2, hgt / 2);
  ctx.rotate(bank);
  ctx.translate(-w / 2, -hgt / 2);
  // drawn wider than the screen, so the corners stay covered while it banks
  const x0 = -w * 0.2, x1 = w * 1.2, y0 = -hgt * 0.3, y1 = hgt * 1.3;
  const sky = ctx.createLinearGradient(0, y0, 0, horizon);
  sky.addColorStop(0, '#050b18'); sky.addColorStop(0.55, '#1a2140'); sky.addColorStop(0.88, '#7a4038'); sky.addColorStop(1, '#d9814a');
  ctx.fillStyle = sky; ctx.fillRect(x0, y0, x1 - x0, horizon - y0);
  const sx = w * 0.68, sun = ctx.createRadialGradient(sx, horizon, 0, sx, horizon, hgt * 0.45);
  sun.addColorStop(0, 'rgba(255,190,110,.75)'); sun.addColorStop(0.25, 'rgba(255,150,80,.25)'); sun.addColorStop(1, 'rgba(255,120,60,0)');
  ctx.fillStyle = sun; ctx.fillRect(x0, y0, x1 - x0, horizon - y0);
  for (const [cx, cy, cw, ph] of seeds) {
    const x = ((cx * 1.6 * w - t * 0.004 * (0.5 + ph)) % (w * 1.6) + w * 1.6) % (w * 1.6) - w * 0.3;
    const y = horizon * (0.35 + cy * 0.5), rw = w * (0.08 + cw * 0.12);
    ctx.fillStyle = `rgba(22,20,40,${(0.35 + ph * 0.3).toFixed(2)})`;
    ctx.beginPath(); ctx.ellipse(x, y, rw, rw * 0.16, 0, 0, 7); ctx.fill();
    ctx.fillStyle = 'rgba(255,150,90,.18)';
    ctx.beginPath(); ctx.ellipse(x + rw * 0.1, y + rw * 0.07, rw * 0.8, rw * 0.06, 0, 0, 7); ctx.fill();
  }
  const ground = ctx.createLinearGradient(0, horizon, 0, y1);
  ground.addColorStop(0, '#2a1d17'); ground.addColorStop(0.3, '#120f0d'); ground.addColorStop(1, '#050606');
  ctx.fillStyle = ground; ctx.fillRect(x0, horizon, x1 - x0, y1 - horizon);
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(255,178,63,.1)';
  ctx.beginPath();
  for (let i = -10; i <= 10; i++) { ctx.moveTo(w / 2, horizon); ctx.lineTo(w / 2 + i * w * 0.16, y1); }
  ctx.stroke();
  for (let k = 0; k < 12; k++) {
    const z = (k + ((t * 0.00035) % 1)) / 12, y = horizon + (y1 - horizon) * z * z;
    ctx.strokeStyle = `rgba(255,178,63,${(z * 0.22).toFixed(3)})`;
    ctx.beginPath(); ctx.moveTo(x0, y); ctx.lineTo(x1, y); ctx.stroke();
  }
  ctx.restore();
  // the windscreen frame does not bank with the world
  ctx.fillStyle = '#0a0c0f';
  ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(w, 0); ctx.lineTo(w, hgt * 0.05);
  ctx.quadraticCurveTo(w / 2, hgt * 0.1, 0, hgt * 0.05); ctx.fill();
  ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(w * 0.05, 0); ctx.lineTo(w * 0.015, hgt); ctx.lineTo(0, hgt); ctx.fill();
  ctx.beginPath(); ctx.moveTo(w, 0); ctx.lineTo(w * 0.95, 0); ctx.lineTo(w * 0.985, hgt); ctx.lineTo(w, hgt); ctx.fill();
};

function frame(now) {
  raf = 0;
  const s = SPEC[mode];
  if (!s) return;
  if (now - last >= 1000 / s.fps - 2) {
    last = now;
    DRAW[mode](now);
  }
  raf = requestAnimationFrame(frame);
}
function stop() { if (raf) cancelAnimationFrame(raf); raf = 0; }
function still() { if (SPEC[mode] && ctx) DRAW[mode](performance.now()); }
function start() {
  stop();
  if (!SPEC[mode] || document.hidden || document.body.classList.contains('night')) return;
  resize();
  if (reduced?.matches) { still(); return; }
  raf = requestAnimationFrame(frame);
}

// The picture is fetched with the panel's headers (the phone's proxy needs them) and shown from a blob URL.
async function loadImage(id) {
  if (id === imageId) return;
  imageId = id;
  if (!id) { setImage(''); return; }
  try {
    const r = await fetch(`${API}/config/background/${encodeURIComponent(id)}`, { headers: PANEL_HEADERS });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const blob = await r.blob();
    if (imageId === id) setImage(URL.createObjectURL(blob));
  } catch (_) {
    if (imageId === id) { imageId = ''; setImage(''); } // try again with the next settings
  }
}
function setImage(url) {
  const old = imageUrl;
  imageUrl = url;
  imgLayer.style.backgroundImage = url ? `url("${url}")` : '';
  document.body.classList.toggle('has-bg-image', !!url);
  if (old) setTimeout(() => URL.revokeObjectURL(old), 1000);
}

// rgb "r, g, b" of the accent → a small set of related colours for the animations
function accentPalette(rgb) {
  const [r, g, b] = (rgb || '45, 212, 191').split(',').map((v) => +v || 0);
  const mix = (k, [tr, tg, tb]) => [r + (tr - r) * k, g + (tg - g) * k, b + (tb - b) * k].map(Math.round).join(',');
  return [`${r},${g},${b}`, mix(0.5, [124, 92, 255]), mix(0.45, [36, 160, 255]), mix(0.6, [255, 255, 255]), mix(0.5, [255, 95, 162])];
}

/** Applies display.background / display.backgroundImage; theme is the panel theme, accent the "r, g, b" accent. */
export function applyBackground(display, theme, accent) {
  layers();
  const id = typeof display.backgroundImage === 'string' && /^[0-9a-f]{16}$/.test(display.backgroundImage) ? display.backgroundImage : '';
  // with a picture of their own, "the theme's own" background is that picture, untinted; a chosen animation plays over it
  const own = { glass: 'blobs', cockpit: 'horizon' }[theme] || 'glow';
  const want = MODES.includes(display.background) ? display.background : id ? 'none' : own;
  palette = theme === 'glass' ? GLASS : accentPalette(accent);
  if (want !== mode) seeds = null;
  mode = want;
  document.body.dataset.bg = mode;
  canvas.hidden = !SPEC[mode];
  start();
  if (!raf) still();
  loadImage(id);
}
