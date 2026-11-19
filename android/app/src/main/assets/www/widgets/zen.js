// ---------------------------------------------------------------- Zen garden theme: brush and stone
// The Zen garden theme draws the clock in brushed ink with an ensō painted round as the minute passes, and gauges
// as an ensō, a stick of incense, a cairn and raked sand (skin-zen.js). Everything here is drawn from numbers only.

const f1 = (v) => v.toFixed(1);
const clamp01 = (v) => Math.max(0, Math.min(1, v || 0));

// Ink that soaks into paper: the edge is roughened a little and a faint bleed spreads round it. Every drawing on a
// page that uses it carries the same definition under the same id, so whichever one the url finds is right.
const INK = '<filter id="pbZenInk" x="-10%" y="-10%" width="120%" height="120%"><feTurbulence type="fractalNoise" baseFrequency=".045" numOctaves="2" seed="3"/>'
  + '<feDisplacementMap in="SourceGraphic" scale="2.6" xChannelSelector="R" yChannelSelector="G" result="d"/>'
  + '<feGaussianBlur in="d" stdDeviation="1.6" result="b"/><feComponentTransfer in="b" result="bleed"><feFuncA type="linear" slope=".35"/></feComponentTransfer>'
  + '<feMerge><feMergeNode in="bleed"/><feMergeNode in="d"/></feMerge></filter>';

// A brush circle that starts thick and dries out towards its end: one wide stroke and a thinner, offset one, both
// drawn round from just right of the top so the gap of the ensō sits where a painter would lift the brush.
const ensoRing = (cls) => `<g class="${cls}" transform="rotate(-80 50 50)">`
  + '<circle class="e1" cx="50" cy="50" r="38" pathLength="100"/><circle class="e2" cx="51" cy="49" r="36.5" pathLength="100"/></g>';

/**
 * Brush digits, set as one drawing so the ink filter runs once per number. Each character sits in its own 64-wide
 * cell, centred, so the width does not depend on which serif the phone has. The minutes also carry the ensō round the
 * seconds; it is painted over the minute by a CSS animation started at the current second, and drawn afresh with the
 * next minute's digits. (The hours carry it too, hidden, since a skin is not told which half it draws.)
 */
export function inkDigits(text) {
  const s = [...String(text)];
  const w = s.length * 64;
  let svg = `<svg class="zen-d" viewBox="0 0 ${w} 100" style="aspect-ratio:${w}/100" aria-hidden="true"><defs>${INK}</defs><g filter="url(#pbZenInk)">`;
  s.forEach((c, i) => {
    const ch = c >= '0' && c <= '9' ? c : '–';
    svg += `<text x="${i * 64 + 32}" y="86">${ch}</text>`;
  });
  svg += '</g></svg>';
  const sec = new Date().getSeconds();
  return svg + `<svg class="zen-enso-s" viewBox="0 0 100 100" aria-hidden="true" style="--zs:-${sec}s">${ensoRing('brush')}</svg>`;
}

/** Two drops of ink for the clock's colon. */
export const INK_COLON = '<svg class="zen-colon" viewBox="0 0 20 100" aria-hidden="true">'
  + '<path d="M10 33c3.4 0 5 2.4 4.6 5-.4 3.2-3 4.6-5.4 4.4C6.6 42 5 40 5.4 37.6 5.8 35 7.4 33 10 33Z"/>'
  + '<path d="M10.4 60c3 .2 4.6 2.6 4.2 5.2-.4 2.8-2.8 4.4-5.2 4.2-2.6-.2-4.2-2.4-3.8-4.8.4-2.8 2.4-4.8 4.8-4.6Z"/></svg>';

/** An ensō painted as far as v (0–1); vermilion when hot. */
const ENSO_SVG = `<svg class="z-enso" viewBox="0 0 100 100" aria-hidden="true"><circle class="ghost" cx="50" cy="50" r="38"/>${ensoRing('brush')}</svg>`;
function enso(svg, v, hot) {
  if (!svg) return;
  const p = clamp01(v);
  // never quite nothing: a dot of ink where the brush touched down
  svg.style.setProperty('--p', f1(Math.max(1.5, p * 100)));
  svg.style.setProperty('--p2', f1(Math.max(1, p * 92)));
  svg.classList.toggle('hot', !!hot);
}

/** A stick of incense in a bowl; the smoke rises higher and thicker with v. */
const INCENSE_SVG = '<svg class="z-incense" viewBox="0 0 80 92" aria-hidden="true">'
  + '<g class="smoke"><path class="sm s1" d="M40 52 C33 44 47 38 40 30 C33 22 47 16 40 8 C36 4 38 0 40 -4"/>'
  + '<path class="sm s2" d="M41 50 C48 42 36 35 43 27 C50 19 38 13 44 4"/>'
  + '<path class="sm s3" d="M39 48 C32 41 41 35 35 27 C30 20 36 12 33 4"/></g>'
  + '<circle class="ember" cx="40" cy="53" r="2.2"/><path class="stick" d="M40 54 V78"/>'
  + '<path class="bowl" d="M20 76 H60 Q58 88 40 88 Q22 88 20 76Z"/><path class="ash" d="M23 76 H57"/></svg>';
function incense(svg, v, hot) {
  const g = svg?.querySelector('.smoke');
  if (!g) return;
  // even at rest a thread of smoke rises; under load it climbs the whole height and spreads
  const k = 0.4 + clamp01(v) * 0.6;
  g.style.transform = `scale(${f1(0.8 + k * 0.35)},${k.toFixed(2)})`;
  g.style.opacity = (0.7 + k * 0.3).toFixed(2);
  svg.classList.toggle('hot', !!hot);
}

/** A cairn of six stones; the more memory is in use, the more of them are stacked. */
const STONES = [[40, 80, 24, 7.5], [41, 67, 19.5, 6.4], [39, 55.5, 16, 5.6], [41, 45.5, 12.8, 4.8], [40, 37, 10, 4.1], [40.5, 29.8, 7.4, 3.4]];
const CAIRN_SVG = '<svg class="z-cairn" viewBox="0 0 80 92" aria-hidden="true"><path class="sand" d="M6 88 Q40 84 74 88"/>'
  + STONES.map(([x, y, rx, ry], i) => `<ellipse class="st s${i % 3}" cx="${x}" cy="${y}" rx="${rx}" ry="${ry}" style="--i:${i}"/>`).join('') + '</svg>';
function cairn(svg, v, hot) {
  if (!svg) return;
  const lit = Math.max(1, Math.round(clamp01(v) * STONES.length));
  svg.querySelectorAll('.st').forEach((el, i) => el.classList.toggle('on', i < lit));
  svg.classList.toggle('hot', !!hot);
}

/** Raked sand: five lines round a rock; the busier the line, the deeper the waves raked into it. */
const SAND_SVG = '<svg class="z-sand" viewBox="0 0 100 70" preserveAspectRatio="none" aria-hidden="true">'
  + [0, 1, 2, 3, 4].map((k) => `<path class="rk r${k}" d=""/>`).join('')
  + '<ellipse class="rock" cx="74" cy="33" rx="9" ry="6.5"/><ellipse class="rock-hi" cx="71.5" cy="30.8" rx="3.6" ry="1.8"/></svg>';
function sand(svg, v, hot) {
  if (!svg) return;
  const a = 0.6 + clamp01(v) * 5.5;
  svg.querySelectorAll('.rk').forEach((p, k) => {
    const y = 15 + k * 10;
    let d = `M0 ${y}`;
    for (let x = 0; x <= 100; x += 5) {
      // the lines part round the rock, the way a rake goes round a stone
      // nearer lines are pushed further, so they bunch up round it in rings instead of crossing one another
      const dy = y - 33, near = Math.cos(Math.min(1, Math.abs(x - 74) / 22) * Math.PI / 2);
      const push = near * (dy < 0 ? -1 : 1) * Math.max(0, 14 * (1 - Math.abs(dy) / 28));
      d += ` L${x} ${f1(y + Math.sin(x * 0.19 + k * 0.6) * a * (1 - near * 0.7) + push)}`;
    }
    p.setAttribute('d', d);
  });
  svg.classList.toggle('hot', !!hot);
}

/**
 * The drawing that stands for a metric: incense for the GPU, a cairn for memory, raked sand for the network, an ensō
 * for everything else. set(holder, v, hot) shows reading v (0–1) in place, so the CSS transitions run on.
 */
export function zenArt(metric) {
  const pick = (html, draw) => ({ html, set: (holder, v, hot) => draw(holder.firstElementChild, v, hot) });
  if (metric === 'gpu') return pick(INCENSE_SVG, incense);
  if (metric === 'ram' || metric === 'vram') return pick(CAIRN_SVG, cairn);
  if (metric === 'net' || metric === 'down' || metric === 'up') return pick(SAND_SVG, sand);
  return pick(ENSO_SVG, enso);
}
