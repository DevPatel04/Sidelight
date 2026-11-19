import { DIG, SEG } from './instruments.js';

// ---------------------------------------------------------------- Nature theme: things that grow
// The Nature theme draws the clock in leaves and gauges as a tree, a campfire and a pond (skin-nature.js).
// Everything here is drawn from numbers only.

const LEAF = ['#8fd16a', '#6fbf5a', '#a9de7a', '#5fae55', '#9ccf63', '#7cc86b', '#b6e486'];
const AUTUMN = ['#f2a541', '#e07a3a', '#f2c14e'];
const f1 = (v) => v.toFixed(1);

// a leaf between two points, as wide as w at its middle
function leafPath(x1, y1, x2, y2, w) {
  const mx = (x1 + x2) / 2, my = (y1 + y2) / 2, len = Math.hypot(x2 - x1, y2 - y1) || 1;
  const px = (-(y2 - y1) / len) * w, py = ((x2 - x1) / len) * w;
  return `M${x1} ${y1} Q${f1(mx + px)} ${f1(my + py)} ${x2} ${y2} Q${f1(mx - px)} ${f1(my - py)} ${x1} ${y1}Z`;
}

// each digit's seven segments are leaves; lit ones sway, each a little out of step with the next
const DIGIT_SVG = DIG.map((lit) => {
  let s = '<svg class="leaf7" viewBox="-3 0 66 104" aria-hidden="true">';
  [...'abcdefg'].forEach((k, i) => {
    const on = lit.includes(k), [x1, y1, x2, y2] = SEG[k];
    s += `<g class="lf ${on ? 'on' : 'off'}" style="--c:${LEAF[i]};--dl:${f1(-i * 0.7)}s"><path class="b" d="${leafPath(x1, y1, x2, y2, 7)}"/>`;
    s += (on ? `<path class="r" d="M${x1} ${y1}L${x2} ${y2}"/>` : '') + '</g>';
  });
  return s + '</svg>';
});

/** Digits grown from leaves; anything that is not a digit shows bare stems. */
export const leafDigits = (text) => [...String(text)].map((c) => (c >= '0' && c <= '9' ? DIGIT_SVG[+c] : DIGIT_SVG[8].replaceAll('lf on', 'lf off'))).join('');

/** Two berries for the clock's colon. */
export const BERRIES = '<svg class="berries" viewBox="0 0 16 104" aria-hidden="true"><circle cx="8" cy="36" r="5"/><circle cx="8" cy="70" r="5"/></svg>';

// the canopy: twelve clumps of leaves, filled from the bottom as the reading rises
const CANOPY = [[32, 56], [48, 56], [24, 46], [40, 44], [56, 46], [18, 34], [32, 32], [48, 32], [62, 34], [26, 22], [54, 22], [40, 14]];
/** A tree whose canopy fills with v (0–1); when hot its leaves turn to autumn colours. */
export function treeSvg(v, hot) {
  const lit = Math.max(1, Math.round(Math.max(0, Math.min(1, v)) * CANOPY.length));
  let s = '<svg class="tree" viewBox="0 0 80 92" aria-hidden="true"><path class="trunk" d="M36 90 L38 60 Q40 54 42 60 L44 90Z"/>';
  s += '<path class="branch" d="M39 72 L30 62 M41 68 L50 58"/>';
  CANOPY.forEach(([x, y], i) => {
    const on = i < lit, c = on ? (hot ? AUTUMN[i % 3] : LEAF[i % 7]) : '';
    s += on ? `<circle class="cn" cx="${x}" cy="${y}" r="9.5" style="fill:${c};--dl:${f1(-i * 0.4)}s"/>` : `<circle class="cn off" cx="${x}" cy="${y}" r="9.5"/>`;
  });
  return s + '<path class="ground" d="M8 90 H72"/></svg>';
}
/** How many clumps a reading lights, so a tree is only redrawn when that changes. */
export const treeKey = (v, hot) => Math.max(1, Math.round(Math.max(0, Math.min(1, v)) * CANOPY.length)) + (hot ? 'h' : '');

/** A campfire; set its height with fire(). */
export const FIRE_SVG = '<svg class="fire" viewBox="0 0 80 84" aria-hidden="true"><g class="flame-size" style="transform:translate(40px,70px) scale(.3,.12)"><g class="flk">'
  + '<path d="M0 0 C-16 -4 -18 -24 -7 -38 C-6 -27 0 -25 2 -33 C4 -44 0 -52 5 -62 C16 -48 20 -26 13 -11 C10 -4 6 0 0 0Z" fill="#f2a541"/>'
  + '<path d="M0 0 C-8 -2 -9 -14 -3 -22 C-2 -16 2 -15 3 -20 C8 -14 9 -6 5 -2 C4 -1 2 0 0 0Z" fill="#ffd96b"/></g></g>'
  + '<rect x="16" y="68" width="48" height="7" rx="3.5" fill="#6b4a2f" transform="rotate(-12 40 72)"/><rect x="16" y="68" width="48" height="7" rx="3.5" fill="#7d5838" transform="rotate(12 40 72)"/>'
  + '<circle class="em" cx="34" cy="62" r="1.6" fill="#f2c14e"/><circle class="em e2" cx="46" cy="60" r="1.3" fill="#f2a541"/><circle class="em e3" cx="40" cy="64" r="1.1" fill="#ffd96b"/></svg>';
/** Burns the fire higher with v (0–1); at 0 it is down to embers. */
export function fire(svg, v) {
  const g = svg?.querySelector('.flame-size');
  const k = 0.12 + Math.max(0, Math.min(1, v)) * 0.95;
  if (g) g.style.transform = `translate(40px,70px) scale(${(0.75 + k * 0.3).toFixed(2)},${k.toFixed(2)})`;
}

/** A stone pond; fill it with pond(). */
export const POND_SVG = '<svg class="pond" viewBox="0 0 90 60" aria-hidden="true"><path class="bed" d="M5 12 Q8 56 45 56 Q82 56 85 12Z"/>'
  // every pond on a page shares this id; the shape is the same in each, so whichever one the url finds is right
  + '<clipPath id="pbPondClip"><path d="M5 12 Q8 56 45 56 Q82 56 85 12Z"/></clipPath>'
  + '<g clip-path="url(#pbPondClip)"><g class="water" style="transform:translateY(54px)"><rect width="90" height="60" fill="#3f8fa0" opacity=".85"/>'
  + '<g class="wv"><path d="M-10 0 Q1 -3 12 0 T34 0 T56 0 T78 0 T100 0 T122 0" fill="none" stroke="#c8ece4" stroke-width="1.3"/></g></g></g>'
  + '<path d="M5 12 Q8 56 45 56 Q82 56 85 12" fill="none" stroke="#8d927e" stroke-width="3" stroke-linecap="round"/>'
  + [[6, 12], [14, 38], [28, 52], [62, 52], [76, 38], [84, 12]].map(([x, y]) => `<ellipse cx="${x}" cy="${y}" rx="4.5" ry="3.2" fill="#a2a892"/>`).join('') + '</svg>';
/** Fills a pond to v (0–1). The bowl narrows towards the bottom, so the level rises with √v to look as full as v. */
export function pond(svg, v) {
  const w = svg?.querySelector('.water');
  if (w) w.style.transform = `translateY(${f1(54 - Math.sqrt(Math.max(0, Math.min(1, v))) * 42)}px)`;
}

/**
 * The drawing that stands for a metric: a campfire for the GPU, a pond for memory, a tree for everything else.
 * set(holder, v, hot) shows reading v (0–1) in the holder element the markup was put in.
 */
export function natureArt(metric) {
  if (metric === 'gpu') return { html: FIRE_SVG, set: (holder, v) => fire(holder.firstElementChild, v) };
  if (metric === 'ram' || metric === 'vram') return { html: POND_SVG, set: (holder, v) => pond(holder.firstElementChild, v) };
  let key = '';
  return {
    html: treeSvg(0, false),
    // the canopy is only drawn again when another clump of leaves fills or the colour turns
    set: (holder, v, hot) => { const k = treeKey(v, hot); if (k !== key) { key = k; holder.innerHTML = treeSvg(v, hot); } },
  };
}
