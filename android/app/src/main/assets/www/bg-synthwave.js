// ---------------------------------------------------------------- background: synthwave (Gaming)
// The Gaming theme's own: a striped sun setting behind wireframe hills, a neon grid floor running toward the viewer,
// and stars. Drawn by background.js at the scale and frame rate below; draw() gets the canvas context, its size in
// pixels, the time in ms and a kit: { palette: ['r,g,b', …], seeded(n): n fixed [x, y, size, phase] in 0–1 }.
// It runs for hours on old phones: gradients are made once per canvas size, and every set of lines is one path.

let cache = null; // the gradients for one canvas size

function gradients(ctx, w, h) {
  const hz = Math.round(h * 0.6), r = Math.min(w * 0.17, h * 0.36), cx = w * 0.5;
  const sky = ctx.createLinearGradient(0, 0, 0, hz);
  sky.addColorStop(0, '#05010f'); sky.addColorStop(0.6, '#1c0736'); sky.addColorStop(1, '#43104f');
  const glow = ctx.createRadialGradient(cx, hz, r * 0.2, cx, hz, r * 2.4);
  glow.addColorStop(0, 'rgba(255,43,214,.32)'); glow.addColorStop(1, 'rgba(255,43,214,0)');
  const sun = ctx.createLinearGradient(0, hz - r, 0, hz);
  sun.addColorStop(0, '#ffe14d'); sun.addColorStop(0.55, '#ff7a8a'); sun.addColorStop(1, '#ff2bd6');
  const floor = ctx.createLinearGradient(0, hz, 0, h);
  floor.addColorStop(0, '#1a0430'); floor.addColorStop(1, '#07021a');
  // hills: a few fixed peaks each side, as fractions of the width and the hill height
  const hill = r * 0.75;
  const peaks = [[0, 0], [0.06, 0.55], [0.12, 0.25], [0.2, 0.9], [0.27, 0.35], [0.34, 0.6], [0.4, 0]];
  return { w, h, hz, r, cx, sky, glow, sun, floor, hill, peaks };
}

export default {
  scale: 0.5,
  fps: 20,
  draw(ctx, w, h, t, kit) {
    if (!cache || cache.w !== w || cache.h !== h) cache = gradients(ctx, w, h);
    const { hz, r, cx, hill, peaks } = cache;

    ctx.fillStyle = cache.sky; ctx.fillRect(0, 0, w, hz);
    // stars twinkle in the upper sky
    const stars = kit.seeded(60);
    ctx.fillStyle = '#ffffff';
    for (let i = 0; i < 60; i++) {
      const [x, y, s, p] = stars[i];
      ctx.globalAlpha = 0.2 + 0.6 * Math.abs(Math.sin(t * 0.0008 + p * 6.3));
      ctx.fillRect(x * w, y * (hz - r - 6), s < 0.8 ? 1 : 1.6, s < 0.8 ? 1 : 1.6);
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = cache.glow; ctx.fillRect(0, 0, w, hz);

    // the sun, cut by dark bands that slide down into the horizon and thin as they go
    ctx.save();
    ctx.beginPath(); ctx.rect(0, 0, w, hz); ctx.clip();
    ctx.fillStyle = cache.sun; ctx.beginPath(); ctx.arc(cx, hz, r, Math.PI, 0); ctx.fill();
    ctx.fillStyle = '#2a0a40';
    const band = r / 7, slide = ((t * 0.006) % band);
    for (let k = 0; k < 7; k++) {
      const y = hz - r * 0.55 + k * band + slide, th = 0.6 + (k / 7) * band * 0.55;
      if (y < hz) ctx.fillRect(cx - r, y, 2 * r, th);
    }
    ctx.restore();

    // wireframe hills on both sides
    ctx.fillStyle = '#12052a'; ctx.strokeStyle = 'rgba(38,240,255,.55)'; ctx.lineWidth = 1;
    ctx.beginPath();
    for (const side of [0, 1]) {
      peaks.forEach(([x, y], i) => {
        const X = side ? w - x * w : x * w, Y = hz - y * hill;
        if (i) ctx.lineTo(X, Y); else ctx.moveTo(X, Y);
      });
    }
    ctx.fill(); ctx.stroke();

    // the floor: lines fanning out from the vanishing point, and cross lines coming toward the viewer
    ctx.fillStyle = cache.floor; ctx.fillRect(0, hz, w, h - hz);
    ctx.strokeStyle = 'rgba(255,43,214,.5)';
    ctx.beginPath();
    for (let i = -16; i <= 16; i++) { ctx.moveTo(cx + i * w * 0.012, hz); ctx.lineTo(cx + i * w * 0.11, h); }
    const run = (t * 0.00035) % 1;
    for (let j = 0; j < 9; j++) {
      const f = (j + run) / 9, y = hz + (h - hz) * f * f;
      ctx.moveTo(0, y); ctx.lineTo(w, y);
    }
    ctx.stroke();
    // the horizon line, bright
    ctx.strokeStyle = '#ff2bd6'; ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.moveTo(0, hz); ctx.lineTo(w, hz); ctx.stroke();
    ctx.lineWidth = 1;
    // a veil so the cards over it stay readable
    ctx.fillStyle = 'rgba(5,0,15,.3)'; ctx.fillRect(0, 0, w, h);
  },
};
