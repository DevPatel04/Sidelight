// ---------------------------------------------------------------- background: ink wash (Zen garden)
// The Zen garden theme's own. Drawn by background.js at the scale and frame rate below; draw() gets the canvas
// context, its size in pixels, the time in ms and a kit: { palette: ['r,g,b', …], seeded(n): n fixed [x, y, size, phase] in 0–1 }.
// A dusk sky with a pale moon, three ranges of ink-wash mountains, a plum branch and a few petals drifting down.
// Everything that does not move is painted once into a cache for the canvas size; a frame is that picture, two bands
// of mist sliding slowly and the petals, so an old phone draws very little per frame.

const W = 780, H = 360; // drawn in this frame, scaled to the canvas
let cache = null, cw = 0, ch = 0;

// a ridge line: a few slow sine waves, different for each range
const ridge = (x, base, amp, k) => H * base - amp * (0.6 * Math.abs(Math.sin(x * 0.0055 + k)) + 0.32 * Math.sin(x * 0.017 + k * 3) + 0.12 * Math.sin(x * 0.05 + k));

function paint(w, h) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const x = c.getContext('2d');
  x.scale(w / W, h / H);
  const sky = x.createLinearGradient(0, 0, 0, H);
  sky.addColorStop(0, '#141319'); sky.addColorStop(0.55, '#24212a'); sky.addColorStop(1, '#2d2629');
  x.fillStyle = sky; x.fillRect(0, 0, W, H);
  // the moon and its halo
  const mx = 612, my = 86, halo = x.createRadialGradient(mx, my, 26, mx, my, 150);
  halo.addColorStop(0, 'rgba(241,230,207,.22)'); halo.addColorStop(1, 'rgba(241,230,207,0)');
  x.fillStyle = halo; x.fillRect(0, 0, W, H);
  x.fillStyle = '#ece0c4'; x.beginPath(); x.arc(mx, my, 34, 0, 7); x.fill();
  x.fillStyle = 'rgba(180,165,140,.18)';
  [[602, 78, 7], [622, 96, 5], [616, 74, 3.5]].forEach(([a, b, r]) => { x.beginPath(); x.arc(a, b, r, 0, 7); x.fill(); });
  // ink-wash ranges: each is dark at its ridge and thins into the mist below, as ink does on wet paper
  [[0.6, 74, [58, 54, 66], 0.9, 1.3], [0.74, 58, [40, 37, 46], 0.95, 2.1], [0.9, 42, [24, 22, 28], 1, 3.4]].forEach(([base, amp, rgb, a, k]) => {
    const top = H * base - amp * 1.05;
    const g = x.createLinearGradient(0, top, 0, H * base + 40);
    g.addColorStop(0, `rgba(${rgb},${a})`); g.addColorStop(0.55, `rgba(${rgb},${(a * 0.82).toFixed(2)})`); g.addColorStop(1, `rgba(${rgb},${(a * 0.55).toFixed(2)})`);
    x.fillStyle = g; x.beginPath(); x.moveTo(0, H);
    for (let px = 0; px <= W; px += 4) x.lineTo(px, ridge(px, base, amp, k));
    x.lineTo(W, H); x.fill();
    // a soft dry-brush edge along the ridge
    x.strokeStyle = `rgba(${rgb.map((v) => Math.max(0, v - 12))},.8)`; x.lineWidth = 1.4; x.beginPath();
    for (let px = 0; px <= W; px += 4) x[px ? 'lineTo' : 'moveTo'](px, ridge(px, base, amp, k));
    x.stroke();
    const mist = x.createLinearGradient(0, H * base - 26, 0, H * base + 24);
    mist.addColorStop(0, 'rgba(236,228,211,0)'); mist.addColorStop(1, 'rgba(236,228,211,.07)');
    x.fillStyle = mist; x.fillRect(0, H * base - 26, W, 50);
  });
  // the plum branch reaching in from the top left, with blossoms and their vermilion hearts
  x.strokeStyle = '#0c0b0e'; x.lineCap = 'round';
  [[7, -10, 38, 80, 26, 152, 58], [4, 72, 40, 112, 10, 176, 18], [2.6, 120, 52, 150, 82, 204, 86], [1.8, 150, 56, 172, 44, 196, 40]].forEach(([lw, a, b, c1, c2, d, e]) => {
    x.lineWidth = lw; x.beginPath(); x.moveTo(a, b); x.quadraticCurveTo(c1, c2, d, e); x.stroke();
  });
  [[60, 34], [96, 42], [130, 52], [152, 60], [112, 24], [142, 16], [170, 18], [162, 76], [192, 84], [86, 28], [194, 42]].forEach(([px, py], i) => {
    x.fillStyle = i % 3 ? 'rgba(232,170,182,.9)' : 'rgba(246,206,214,.9)';
    for (let p = 0; p < 5; p++) {
      const an = (p / 5) * Math.PI * 2 + i;
      x.beginPath(); x.arc(px + Math.cos(an) * 2.6, py + Math.sin(an) * 2.6, 2.3, 0, 7); x.fill();
    }
    x.fillStyle = '#d9482b'; x.beginPath(); x.arc(px, py, 1.1, 0, 7); x.fill();
  });
  // a dusk veil, so the cards read over everything
  x.fillStyle = 'rgba(10,9,12,.22)'; x.fillRect(0, 0, W, H);
  return c;
}

export default {
  scale: 0.5,
  fps: 12,
  draw(ctx, w, h, t) {
    if (!cache || cw !== w || ch !== h) { cache = paint(w, h); cw = w; ch = h; }
    ctx.drawImage(cache, 0, 0);
    ctx.save();
    ctx.scale(w / W, h / H);
    // two bands of mist drifting across the valleys at their own pace
    [[0.66, 0.006, 0.05], [0.83, 0.011, 0.06]].forEach(([y, sp, a], i) => {
      const off = (t * sp) % (W * 2);
      for (let k = -1; k < 3; k++) {
        const cx = k * 420 - off * 0.5 + i * 160, g = ctx.createRadialGradient(cx, H * y, 10, cx, H * y, 200);
        g.addColorStop(0, `rgba(236,228,211,${a})`); g.addColorStop(1, 'rgba(236,228,211,0)');
        ctx.fillStyle = g; ctx.fillRect(cx - 200, H * y - 60, 400, 120);
      }
    });
    // petals falling from the branch and drifting right
    for (let i = 0; i < 14; i++) {
      const px = ((i * 89 + t * 0.012 * (1 + (i % 3))) % (W + 40)) - 20 + Math.sin(t * 0.0009 + i) * 14;
      const py = ((i * 53 + t * 0.018 * (1 + (i % 2))) % (H + 20)) - 10;
      ctx.save(); ctx.translate(px, py); ctx.rotate(t * 0.001 + i);
      ctx.globalAlpha = 0.7; ctx.fillStyle = i % 2 ? '#e8aab6' : '#f6ced6';
      ctx.beginPath(); ctx.ellipse(0, 0, 3.6, 2, 0, 0, 7); ctx.fill(); ctx.restore();
    }
    ctx.restore();
  },
};
