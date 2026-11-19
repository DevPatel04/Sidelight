// ---------------------------------------------------------------- background: city in the rain (Rainy city)
// The Rainy city theme's own. Drawn by background.js at the scale and frame rate below; draw() gets the canvas
// context, its size in pixels, the time in ms and a kit: { palette: ['r,g,b', …], seeded(n): n fixed [x, y, size, phase] in 0–1 }.
// A night skyline with lit windows and neon signs, its reflection shimmering in the wet street, slanted rain, rings
// in the puddles and, now and then, one soft flash of lightning far away. The city itself never changes, so it is
// drawn once per size into canvases of its own and each frame only copies them and adds what moves.

const AMBER = '255,196,107', PINK = '255,79,139', TEAL = '94,224,255';
let cache = null; // { w, h, city, refl, ground, flicker: [[x, y, w, h, phase]] }

function rng(seed) {
  let s = seed;
  return () => (s = (s * 16807) % 2147483647) / 2147483647;
}

function build(w, h) {
  const G = Math.round(h * 0.8), r = rng(11), u = h / 100; // sizes go by height, so any shape of screen looks alike
  const city = document.createElement('canvas');
  city.width = w; city.height = G;
  const c = city.getContext('2d');
  const sky = c.createLinearGradient(0, 0, 0, G);
  sky.addColorStop(0, '#060914'); sky.addColorStop(0.6, '#151530'); sky.addColorStop(1, '#3a2342');
  c.fillStyle = sky; c.fillRect(0, 0, w, G);
  // low cloud lit orange from below by the city
  const glow = c.createRadialGradient(w * 0.55, G, 0, w * 0.55, G, h * 0.7);
  glow.addColorStop(0, 'rgba(255,140,90,.18)'); glow.addColorStop(1, 'rgba(255,140,90,0)');
  c.fillStyle = glow; c.fillRect(0, 0, w, G);

  const flicker = [];
  // far, middle and near rows of buildings: darker and with more windows lit the nearer they are
  [[38, 62, '#151a31', 0.35], [24, 50, '#0e1124', 0.55], [12, 34, '#080a16', 0.8]].forEach(([lo, hi, col, lit], layer) => {
    let x = -r() * 6 * u;
    while (x < w) {
      const bw = (6 + r() * 11) * u, bh = (lo + r() * (hi - lo)) * u, top = G - bh;
      c.fillStyle = col; c.fillRect(x, top, bw, bh);
      if (r() < 0.3) c.fillRect(x + bw * 0.4, top - 4 * u, Math.max(1, 0.4 * u), 4 * u); // an aerial
      const ws = Math.max(1, 0.9 * u), wh = Math.max(1, 1.2 * u);
      for (let wy = top + 2 * u; wy < G - 2 * u; wy += 2.6 * u) {
        for (let wx = x + 1.4 * u; wx < x + bw - 1.6 * u; wx += 2.2 * u) {
          const k = r();
          if (k > lit * 0.42) continue;
          c.fillStyle = k < 0.03 ? `rgba(${TEAL},.55)` : `rgba(${AMBER},${(0.35 + layer * 0.2 + r() * 0.3).toFixed(2)})`;
          c.fillRect(wx, wy, ws, wh);
          if (layer === 2 && r() < 0.04) flicker.push([wx, wy, ws, wh, r() * 100]);
        }
      }
      // a neon sign on some of the near buildings, hung down the side like a shop sign
      if (layer === 2 && r() < 0.28 && bh > 16 * u) {
        const col2 = r() < 0.5 ? PINK : TEAL, sx = x + bw * 0.15, sy = top + 3 * u, sw = 2.4 * u, sh = Math.min(bh * 0.5, 14 * u);
        c.shadowColor = `rgba(${col2},.9)`; c.shadowBlur = 3 * u;
        c.strokeStyle = `rgba(${col2},.95)`; c.lineWidth = Math.max(1, 0.5 * u);
        c.strokeRect(sx, sy, sw, sh);
        c.fillStyle = `rgba(${col2},.5)`;
        for (let k = 1; k < 4; k++) c.fillRect(sx + sw * 0.3, sy + (sh * k) / 4 - 0.5 * u, sw * 0.4, u);
        c.shadowBlur = 0;
      }
      x += bw + r() * 2 * u;
    }
  });
  // street lamps along the kerb: a pool of sodium light each
  for (let lx = w * 0.08; lx < w; lx += w * 0.22) {
    c.fillStyle = '#05060c'; c.fillRect(lx, G - 16 * u, Math.max(1, 0.5 * u), 16 * u);
    const p = c.createRadialGradient(lx, G - 16 * u, 0, lx, G - 16 * u, 12 * u);
    p.addColorStop(0, `rgba(${AMBER},.55)`); p.addColorStop(0.15, `rgba(${AMBER},.22)`); p.addColorStop(1, `rgba(${AMBER},0)`);
    c.fillStyle = p; c.fillRect(lx - 12 * u, G - 28 * u, 24 * u, 28 * u);
  }

  // the reflection: the city upside down and squashed into the street, dimmed and tinted by the wet tarmac
  const rh = h - G, refl = document.createElement('canvas');
  refl.width = w; refl.height = rh;
  const q = refl.getContext('2d');
  q.save(); q.translate(0, rh); q.scale(1, -rh / G * 1.4); q.globalAlpha = 0.45; q.drawImage(city, 0, 0); q.restore();
  const wet = q.createLinearGradient(0, 0, 0, rh);
  wet.addColorStop(0, 'rgba(14,12,28,.35)'); wet.addColorStop(1, 'rgba(4,5,12,.9)');
  q.fillStyle = wet; q.fillRect(0, 0, w, rh);
  return { w, h, G, u, city, refl, flicker };
}

export default {
  scale: 0.5,
  fps: 20,
  draw(ctx, w, h, t) {
    if (!cache || cache.w !== w || cache.h !== h) cache = build(w, h);
    const { G, u, city, refl, flicker } = cache, rh = h - G;
    ctx.fillStyle = '#05060c'; ctx.fillRect(0, 0, w, h);
    ctx.drawImage(city, 0, 0);
    // a few windows go dark and light again, slowly, like people moving between rooms
    for (const [x, y, ww, wh, ph] of flicker) {
      const a = Math.sin(t * 0.0004 + ph);
      if (a < -0.3) { ctx.fillStyle = '#080a16'; ctx.fillRect(x, y, ww, wh); }
    }
    // the reflection, in strips that sway a little each, so the street shimmers
    const strips = 12, sh = rh / strips;
    for (let i = 0; i < strips; i++) {
      const dx = Math.sin(t * 0.0018 + i * 0.9) * (0.3 + i * 0.12) * u;
      ctx.drawImage(refl, 0, i * sh, w, sh + 1, dx, G + i * sh, w, sh + 1);
    }
    ctx.fillStyle = `rgba(${AMBER},.18)`; ctx.fillRect(0, G, w, Math.max(1, 0.3 * u)); // the wet kerb catching the light

    // rain: two depths of slanted streaks, all in one path each
    for (const [n, len, sp, a, lw] of [[90, 4, 0.11, 0.22, 0.25], [50, 7, 0.17, 0.32, 0.4]]) {
      ctx.strokeStyle = `rgba(190,210,255,${a})`; ctx.lineWidth = Math.max(1, lw * u);
      ctx.beginPath();
      for (let i = 0; i < n; i++) {
        const y = ((i * 37.7 * u + t * sp * u) % (h + len * 2 * u)) - len * u;
        const x = (((i * 61.3 * u - t * sp * 0.2 * u) % w) + w) % w;
        ctx.moveTo(x, y); ctx.lineTo(x - len * 0.25 * u, y + len * u);
      }
      ctx.stroke();
    }
    // rings where the drops land in the puddles
    ctx.strokeStyle = 'rgba(190,210,255,.3)'; ctx.lineWidth = Math.max(1, 0.25 * u);
    for (let i = 0; i < 9; i++) {
      const p = (t * 0.0011 + i * 0.37) % 1, x = ((i * 0.137 + Math.floor(t * 0.0011 + i * 0.37) * 0.29) % 1) * w;
      const y = G + rh * (0.15 + ((i * 0.23) % 0.8)), rr = (0.6 + p * 4) * u;
      ctx.globalAlpha = 1 - p;
      ctx.beginPath(); ctx.ellipse(x, y, rr, rr * 0.3, 0, 0, 7); ctx.stroke();
    }
    ctx.globalAlpha = 1;

    // lightning far off: about once every 40 s, one soft glow over the sky that fades in under a second — never a
    // strobe, the panel sits on a desk for hours
    const ph = t % 41000;
    if (ph < 900) {
      const a = 0.16 * Math.exp(-ph / 220);
      const f = ctx.createLinearGradient(0, 0, 0, G);
      f.addColorStop(0, `rgba(200,210,255,${a.toFixed(3)})`); f.addColorStop(1, 'rgba(200,210,255,0)');
      ctx.fillStyle = f; ctx.fillRect(0, 0, w, G);
    }
    // a veil, so cards and text over the city stay easy to read
    ctx.fillStyle = 'rgba(4,6,14,.22)'; ctx.fillRect(0, 0, w, h);
  },
};
