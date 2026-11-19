// ---------------------------------------------------------------- Gaming theme: arcade pieces
// The Gaming theme draws the clock in a 5 × 7 pixel font, like an arcade scoreboard, and gauges as segmented
// HP bars, a boost equalizer and an inventory of slots (skin-gaming.js). Everything here is drawn from numbers only.

// a 5 × 7 font: each digit is seven rows of five pixels
const FONT = [
  ['01110', '10001', '10011', '10101', '11001', '10001', '01110'],
  ['00100', '01100', '00100', '00100', '00100', '00100', '01110'],
  ['01110', '10001', '00001', '00110', '01000', '10000', '11111'],
  ['11110', '00001', '00001', '01110', '00001', '00001', '11110'],
  ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
  ['11111', '10000', '11110', '00001', '00001', '10001', '01110'],
  ['00110', '01000', '10000', '11110', '10001', '10001', '01110'],
  ['11111', '00001', '00010', '00100', '01000', '01000', '01000'],
  ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
  ['01110', '10001', '10001', '01111', '00001', '00010', '01100'],
];
const BLANK = Array(7).fill('00000');
const pix = (x, y) => `M${x * 10 + 0.8} ${y * 10 + 0.8}h8.4v8.4h-8.4z`;

// lit and dark pixels as one path each, so a digit is two nodes however many pixels it has
function digitSvg(rows) {
  let lit = '', dark = '';
  rows.forEach((row, y) => [...row].forEach((b, x) => { if (b === '1') lit += pix(x, y); else dark += pix(x, y); }));
  return `<svg class="px7" viewBox="0 0 50 70" aria-hidden="true"><path class="unl" d="${dark}"/><path class="lit" d="${lit}"/></svg>`;
}
const DIGIT_SVG = FONT.map(digitSvg);
const BLANK_SVG = digitSvg(BLANK);

/** Digits as pixel-font svg markup; anything that is not a digit (a blank hour, "--") shows the dark grid. */
export const pixelDigits = (text) => [...String(text)].map((c) => (c >= '0' && c <= '9' ? DIGIT_SVG[+c] : BLANK_SVG)).join('');

/** Two pixels for the clock's colon. */
export const PIXEL_COLON = `<svg class="pxc" viewBox="0 0 10 70" aria-hidden="true"><path d="${pix(0, 2)}${pix(0, 4)}"/></svg>`;

// ---------------------------------------------------------------- meters
// Each picture comes twice: a wide strip for the bottom of a metric tile and a square for a gauge. The theme's sheet
// shows the one that fits where the holder is; set() updates both, which is a handful of attribute changes.
const f1 = (v) => v.toFixed(1);
const clamp01 = (v) => Math.max(0, Math.min(1, v || 0));

// an HP bar: twenty blocks, and a ring of twenty-four blocks round 270° for a gauge
function segStrip() {
  let s = '<svg class="gx gx-strip gx-segs" viewBox="0 0 200 12" preserveAspectRatio="none" aria-hidden="true">';
  for (let i = 0; i < 20; i++) s += `<rect class="sg" x="${i * 10 + 1}" y="0" width="8" height="12"/>`;
  return s + '</svg>';
}
function segRing() {
  const n = 24, r = 40, a0 = 0.75 * Math.PI, step = (1.5 * Math.PI) / n, gap = 0.045;
  let s = '<svg class="gx gx-sq gx-ring" viewBox="0 0 100 100" aria-hidden="true">';
  for (let i = 0; i < n; i++) {
    const a = a0 + i * step + gap, b = a0 + (i + 1) * step - gap;
    s += `<path class="sg" d="M${f1(50 + Math.cos(a) * r)} ${f1(50 + Math.sin(a) * r)}A${r} ${r} 0 0 1 ${f1(50 + Math.cos(b) * r)} ${f1(50 + Math.sin(b) * r)}"/>`;
  }
  return s + '</svg>';
}
// a boost equalizer: bars that stand on the floor and are scaled up from it
function eq(n, w, h, cls) {
  let s = `<svg class="gx ${cls} gx-eq" viewBox="0 0 ${n * w} ${h}" preserveAspectRatio="none" aria-hidden="true">`;
  for (let i = 0; i < n; i++) s += `<rect class="eb" x="${i * w + 1}" y="0" width="${w - 2}" height="${h}" style="transform:scaleY(.04)"/>`;
  return s + '</svg>';
}
// an inventory: slots that light up as memory fills
function slots(cols, rows, cls) {
  let s = `<svg class="gx ${cls} gx-inv" viewBox="0 0 ${cols * 10} ${rows * 10}" aria-hidden="true">`;
  for (let y = 0; y < rows; y++) for (let x = 0; x < cols; x++) s += `<rect class="sg" x="${x * 10 + 1}" y="${y * 10 + 1}" width="8" height="8"/>`;
  return s + '</svg>';
}

// blocks light from the first; past 60% of the scale they run yellow and past 85% pink, like a health bar in danger
function lightBlocks(svg, v, colourBands) {
  const sg = svg.querySelectorAll('.sg'), n = sg.length, lit = Math.round(v * n);
  sg.forEach((el, i) => {
    let c = 'sg';
    if (i < lit) c += !colourBands ? ' on' : i >= n * 0.85 ? ' on h' : i >= n * 0.6 ? ' on w' : ' on';
    if (el.getAttribute('class') !== c) el.setAttribute('class', c);
  });
}
// the bars bunch up in the middle like a spectrum and jump a little every update
function jump(svg, v) {
  const eb = svg.querySelectorAll('.eb'), n = eb.length, mid = (n - 1) / 2;
  eb.forEach((el, i) => {
    const shape = 1 - (Math.abs(i - mid) / (mid + 1)) * 0.55;
    el.style.transform = `scaleY(${Math.max(0.04, Math.min(1, v * shape * (0.7 + Math.random() * 0.45))).toFixed(2)})`;
  });
}

/**
 * The picture for a metric: an equalizer for the GPU, an inventory for memory, an HP bar (a ring of blocks on a gauge)
 * for everything else. set(holder, v, hot) shows reading v (0–1) in the holder element the markup was put in.
 */
export function gamingArt(metric) {
  const kind = metric === 'gpu' ? 'eq' : metric === 'ram' || metric === 'vram' ? 'inv' : 'segs';
  const html = kind === 'eq' ? eq(18, 10, 40, 'gx-strip') + eq(9, 10, 60, 'gx-sq')
    : kind === 'inv' ? slots(16, 2, 'gx-strip') + slots(5, 5, 'gx-sq')
    : segStrip() + segRing();
  return {
    html,
    set(holder, v, hot) {
      v = clamp01(v);
      for (const svg of holder.children) {
        if (kind === 'eq') jump(svg, v);
        else lightBlocks(svg, v, kind === 'segs');
        svg.classList.toggle('hot', !!hot);
      }
    },
  };
}
