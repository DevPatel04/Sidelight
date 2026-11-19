// ---------------------------------------------------------------- background: notebook (Paper sketch)
// The Paper sketch theme's own. Drawn by background.js at the scale and frame rate below; draw() gets the canvas
// context, its size in pixels, the time in ms and a kit: { palette: ['r,g,b', …], seeded(n): n fixed [x, y, size, phase] in 0–1 }.
// Ruled notebook paper with a red margin, a coffee ring and a paper plane drifting slowly. The paper is still, so a
// few frames a second are plenty. The ring and the plane keep to the margins round the pages, so they never sit
// behind the clock or other text written straight on the paper.
export default {
  scale: 0.5,
  fps: 8,
  draw(ctx, w, h, t, kit) {
    const s = w / Math.max(1, innerWidth); // canvas pixels per CSS pixel
    ctx.fillStyle = '#f5eedb';
    ctx.fillRect(0, 0, w, h);
    // paper grain: fixed faint flecks
    ctx.fillStyle = 'rgba(120, 95, 55, .07)';
    for (const [x, y, z] of kit.seeded(90)) ctx.fillRect(x * w, y * h, 1 + z * 1.5, 1);
    // ruled lines every 24 CSS px and the margin
    ctx.strokeStyle = 'rgba(70, 110, 190, .2)';
    ctx.lineWidth = Math.max(1, s);
    ctx.beginPath();
    for (let y = 30 * s; y < h; y += 24 * s) { ctx.moveTo(0, Math.round(y) + 0.5); ctx.lineTo(w, Math.round(y) + 0.5); }
    ctx.stroke();
    ctx.strokeStyle = 'rgba(200, 70, 60, .32)';
    ctx.beginPath(); ctx.moveTo(Math.round(46 * s) + 0.5, 0); ctx.lineTo(Math.round(46 * s) + 0.5, h); ctx.stroke();
    // a coffee ring in the bottom-left corner, mostly in the margin the pages leave free
    const rx = 18 * s, ry = h - 10 * s, rr = 34 * s;
    ctx.strokeStyle = 'rgba(150, 100, 55, .16)';
    ctx.lineWidth = 4 * s; ctx.beginPath(); ctx.arc(rx, ry, rr, 0.4, 5.7); ctx.stroke();
    ctx.lineWidth = 1.5 * s; ctx.beginPath(); ctx.arc(rx + 2 * s, ry + 2 * s, rr * 0.86, 0, 6.3); ctx.stroke();
    // the paper plane: one slow pass every two minutes along the strip under the pages, where nothing is written
    const u = (t * 0.0000085) % 1, base = h - 8.5 * s;
    const at = (k) => [w * (-0.05 + k * 1.1), base + Math.sin(k * Math.PI * 6) * 1.5 * s];
    ctx.setLineDash([4 * s, 6 * s]); ctx.strokeStyle = 'rgba(43, 47, 74, .28)'; ctx.lineWidth = 1.2 * s;
    ctx.beginPath();
    for (let k = 0; k <= 24; k++) { const [x, y] = at(u - 0.12 + (k * 0.12) / 24); k ? ctx.lineTo(x, y) : ctx.moveTo(x, y); }
    ctx.stroke(); ctx.setLineDash([]);
    const [x, y] = at(u), [x2, y2] = at(u + 0.002), k = 0.6 * s;
    ctx.save(); ctx.translate(x, y); ctx.rotate(Math.atan2(y2 - y, x2 - x)); ctx.scale(k, k);
    ctx.fillStyle = '#fffdf6'; ctx.strokeStyle = 'rgba(43, 47, 74, .75)'; ctx.lineWidth = 1.6;
    ctx.beginPath(); ctx.moveTo(12, 0); ctx.lineTo(-10, -8); ctx.lineTo(-5, 0); ctx.lineTo(-10, 8); ctx.closePath(); ctx.fill(); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(12, 0); ctx.lineTo(-5, 0); ctx.stroke();
    ctx.restore();
  },
};
