// ---------------------------------------------------------------- background: galaxy (Space)
// The Space theme's own: deep-space gradient, nebulae, three layers of twinkling stars drifting at their own speeds, a
// banded planet low on the right with a small moon going round it, and now and then a shooting star.
// Drawn by background.js at the scale and frame rate below; draw() gets the canvas context, its size in pixels, the time
// in ms and a kit: { palette, seeded(n): n fixed [x, y, size, phase] in 0–1 }.
// It runs for hours on old phones: everything that does not move (sky, nebulae, planet) is painted once per size into
// offscreen canvases, so a frame is two image copies, the stars as plain rectangles and a couple of small shapes.

let sky = null, planet = null, sw = 0, sh = 0, pr = 0;

const NEBULAE = [[0.28, 0.33, 0.34, '120,80,255', 0.24], [0.72, 0.25, 0.3, '40,140,255', 0.17], [0.54, 0.86, 0.32, '255,90,170', 0.13]];

function paintSky(w, h) {
  sky ||= document.createElement('canvas');
  sky.width = w; sky.height = h;
  const c = sky.getContext('2d'), m = Math.max(w, h);
  const g = c.createLinearGradient(0, 0, w, h);
  g.addColorStop(0, '#05041a'); g.addColorStop(0.6, '#0a0a24'); g.addColorStop(1, '#140a26');
  c.fillStyle = g; c.fillRect(0, 0, w, h);
  for (const [x, y, r, col, a] of NEBULAE) {
    const rg = c.createRadialGradient(x * w, y * h, 0, x * w, y * h, r * m);
    rg.addColorStop(0, `rgba(${col},${a})`); rg.addColorStop(0.55, `rgba(${col},${(a * 0.35).toFixed(3)})`); rg.addColorStop(1, `rgba(${col},0)`);
    c.fillStyle = rg; c.fillRect(0, 0, w, h);
  }
  // a faint band of the galaxy across the sky
  c.save(); c.translate(w * 0.5, h * 0.5); c.rotate(-0.32);
  const band = c.createLinearGradient(0, -h * 0.22, 0, h * 0.22);
  band.addColorStop(0, 'rgba(180,170,255,0)'); band.addColorStop(0.5, 'rgba(180,170,255,.07)'); band.addColorStop(1, 'rgba(180,170,255,0)');
  c.fillStyle = band; c.fillRect(-m, -h * 0.22, m * 2, h * 0.44);
  c.restore();
}

function paintPlanet(r) {
  const d = Math.ceil(r * 2.6);
  planet ||= document.createElement('canvas');
  planet.width = d; planet.height = d;
  const c = planet.getContext('2d'), x = d / 2, y = d / 2;
  const ag = c.createRadialGradient(x, y, r * 0.9, x, y, r * 1.25);
  ag.addColorStop(0, 'rgba(124,196,255,.35)'); ag.addColorStop(1, 'rgba(124,196,255,0)');
  c.fillStyle = ag; c.beginPath(); c.arc(x, y, r * 1.25, 0, 7); c.fill();
  const pg = c.createRadialGradient(x - r * 0.42, y - r * 0.58, r * 0.08, x, y, r);
  pg.addColorStop(0, '#5a6bd6'); pg.addColorStop(0.6, '#25226a'); pg.addColorStop(1, '#0c0a2a');
  c.fillStyle = pg; c.beginPath(); c.arc(x, y, r, 0, 7); c.fill();
  c.save(); c.beginPath(); c.arc(x, y, r, 0, 7); c.clip();
  c.strokeStyle = 'rgba(180,140,255,.18)'; c.lineWidth = Math.max(1, r * 0.05);
  for (let k = -4; k < 5; k++) {
    const yy = y + k * r * 0.2;
    c.beginPath(); c.moveTo(x - r, yy); c.bezierCurveTo(x - r * 0.3, yy - r * 0.08, x + r * 0.3, yy + r * 0.08, x + r, yy); c.stroke();
  }
  // night side
  const sg = c.createLinearGradient(x - r, y - r, x + r * 0.6, y + r);
  sg.addColorStop(0.45, 'rgba(0,0,10,0)'); sg.addColorStop(1, 'rgba(0,0,10,.7)');
  c.fillStyle = sg; c.fillRect(x - r, y - r, r * 2, r * 2);
  c.restore();
}

export default {
  scale: 0.5,
  fps: 20,
  draw(ctx, w, h, t, kit) {
    if (w !== sw || h !== sh) {
      sw = w; sh = h; pr = Math.min(w, h) * 0.34;
      paintSky(w, h); paintPlanet(pr);
    }
    ctx.drawImage(sky, 0, 0);
    // stars: three depths drift left at their own speeds; the nearest are larger and some are warm
    const stars = kit.seeded(150);
    for (let i = 0; i < stars.length; i++) {
      const [sx, sy, sz, ph] = stars[i], k = i % 3;
      const x = ((sx * w - t * 0.0015 * (k + 1)) % w + w) % w, y = sy * h;
      ctx.globalAlpha = (0.3 + 0.7 * Math.abs(Math.sin(t * 0.0009 * (0.4 + ph) + ph * 40))) * (0.45 + k * 0.25);
      ctx.fillStyle = k === 2 && i % 5 === 0 ? '#ffd9a8' : sz > 0.85 ? '#cfe3ff' : '#e8ecff';
      const s = k === 2 && sz > 0.7 ? 1.6 : k === 0 ? 0.6 : 1;
      ctx.fillRect(x, y, s, s);
    }
    ctx.globalAlpha = 1;
    // the planet, low on the right, with a small moon that passes in front of it and behind it
    const px = w * 0.86, py = h + pr * 0.25, a = t * 0.00005;
    const mx = px + Math.cos(a) * pr * 1.5, my = py - pr * 0.55 + Math.sin(a) * pr * 0.32, front = Math.sin(a) > 0;
    if (front) ctx.drawImage(planet, px - planet.width / 2, py - planet.height / 2);
    ctx.fillStyle = '#cfd3e6'; ctx.beginPath(); ctx.arc(mx, my, pr * 0.07, 0, 7); ctx.fill();
    if (!front) ctx.drawImage(planet, px - planet.width / 2, py - planet.height / 2);
    // a shooting star every 11 s, for under a second
    const p = (t % 11000) / 900;
    if (p < 1) {
      const x0 = w * (0.15 + 0.4 * p), y0 = h * (0.08 + 0.25 * p), len = w * 0.08;
      ctx.globalAlpha = 1 - p;
      ctx.strokeStyle = '#ffffff'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x0 - len, y0 - len * 0.3); ctx.stroke();
      ctx.globalAlpha = 1;
    }
  },
};
