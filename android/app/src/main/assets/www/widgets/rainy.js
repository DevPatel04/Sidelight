// ---------------------------------------------------------------- Rainy city theme: the street at night
// The Rainy city theme draws the clock as a lit street sign with rain running down it, and gauges as things on a
// wet street: a tower block, a traffic light, a rain gauge and a skyline (skin-rainy.js). Everything here is drawn
// from numbers only, never from agent text.

const f1 = (v) => v.toFixed(1);
const clamp01 = (v) => Math.max(0, Math.min(1, v || 0));

// condensed sign digits as single strokes in a 44 × 104 cell, drawn as glowing tubes; a squashed, mirrored copy
// under them is the reflection on the wet street (the cell is 134 tall to hold it)
const GLYPH = [
  'M8 22A14 14 0 0 1 36 22V82A14 14 0 0 1 8 82Z',
  'M12 21L25 8V96',
  'M9 20Q11 8 22 8Q36 8 36 22V30Q36 40 30 50L8 96H37',
  'M9 18Q12 8 22 8Q36 8 36 22V36Q36 50 22 50H17M22 50Q36 50 36 64V82Q36 96 22 96Q12 96 9 86',
  'M29 96V8L7 67H38',
  'M36 8H10L9 47Q14 42 22 42Q36 42 36 58V82Q36 96 22 96Q12 96 9 86',
  'M33 15Q30 8 22 8Q8 8 8 22V82M8 60A14 14 0 0 1 36 60V82A14 14 0 0 1 8 82Z',
  'M7 8H37L17 96',
  'M22 50Q9 50 9 37V22Q9 8 22 8Q35 8 35 22V37Q35 50 22 50Q8 50 8 64V82Q8 96 22 96Q36 96 36 82V64Q36 50 22 50Z',
  'M36 44A14 14 0 0 1 8 44V22A14 14 0 0 1 36 22ZM36 22V82Q36 96 22 96Q14 96 11 89',
];
// where rain runs down each digit: on its upright strokes, so the drops look like they run over the tubes
const RUN = [[8, 36], [25], [36, 22], [36, 22], [29], [9, 36], [8, 36], [27], [8, 36], [36, 8]];
const REFLECT = 'matrix(1 0 0 -.28 0 132)';

function glyphSvg(d, runs, dark) {
  let s = '<svg class="sign7" viewBox="0 0 44 134" aria-hidden="true">';
  s += `<g class="refl" transform="${REFLECT}"><path d="${d}"/></g>`;
  s += `<path class="halo" d="${d}"/><path class="tube${dark ? ' dark' : ''}" d="${d}"/>`;
  if (!dark) {
    // two drops per digit, each a little out of step, so a row of digits never drips in time
    runs.concat(runs.length < 2 ? [runs[0] - 6] : []).slice(0, 2).forEach((x, i) => {
      s += `<g class="drop" style="--dl:${f1(-(x * 0.37 + i * 1.9) % 4)}s;--dd:${f1(3.2 + ((x + i * 7) % 5) * 0.45)}s">`
        + `<path d="M${x} 2q3.4 5 0 8q-3.4-3 0-8z"/></g>`;
    });
  }
  return s + '<path class="wet" d="M0 108H44M3 116H41M1 124H43"/></svg>';
}
const DIGIT_SVG = GLYPH.map((d, i) => glyphSvg(d, RUN[i], false));
const DARK_SVG = glyphSvg(GLYPH[8], [], true);

/** Digits as lit street-sign tubes with rain running down them; anything that is not a digit is an unlit sign. */
export const signDigits = (text) => [...String(text)].map((c) => (c >= '0' && c <= '9' ? DIGIT_SVG[+c] : DARK_SVG)).join('');

/** Two sodium lamps for the clock's colon, with their reflections. */
export const LAMPS = '<svg class="lamps" viewBox="0 0 14 134" aria-hidden="true">'
  + '<circle class="glow" cx="7" cy="36" r="6.5"/><circle class="glow" cx="7" cy="70" r="6.5"/>'
  + '<circle class="lamp" cx="7" cy="36" r="3.6"/><circle class="lamp" cx="7" cy="70" r="3.6"/>'
  + '<g class="refl" transform="matrix(1 0 0 -.28 0 132)"><circle cx="7" cy="36" r="3.6"/><circle cx="7" cy="70" r="3.6"/></g></svg>';

// ---------------------------------------------------------------- tower block (CPU and anything else with a scale)
// 50 windows that light in a scattered order as the load rises, so the block fills like a real one at dusk
const ORDER = Array.from({ length: 50 }, (_, i) => (i * 29 + 7) % 50);
const TOWER_SVG = (() => {
  let s = '<svg class="tower" viewBox="0 0 80 92" aria-hidden="true">'
    + '<path class="mast" d="M45 2V11"/><circle class="beacon" cx="45" cy="2.6" r="2"/>'
    + '<path class="block" d="M20 92V15L45 10L62 15V92Z"/><path class="side" d="M62 15L70 19V92H62Z"/>';
  for (const i of ORDER) s += `<rect class="wn" x="${f1(24.5 + (i % 5) * 7)}" y="${f1(19 + Math.floor(i / 5) * 7.1)}" width="4.6" height="4.4"/>`;
  return s + '<path class="kerb" d="M8 91.5H76"/></svg>';
})();
function tower(svg, v, hot) {
  if (!svg) return;
  const n = Math.round(clamp01(v) * 50);
  svg.classList.toggle('hot', !!hot);
  // windows are in lighting order, so the first n are lit; only the ones that change are touched
  svg.querySelectorAll('.wn').forEach((r, k) => { if (r.classList.contains('on') !== k < n) r.classList.toggle('on', k < n); });
}

// ---------------------------------------------------------------- traffic light (GPU)
const SIGNAL_SVG = '<svg class="signal" viewBox="0 0 80 92" aria-hidden="true"><path class="pole" d="M40 70V90"/>'
  + '<rect class="hood" x="27" y="4" width="26" height="66" rx="8"/>'
  + [['stop', 18], ['wait', 37], ['go', 56]].map(([k, y]) => `<circle class="halo ${k}" cx="40" cy="${y}" r="14"/><circle class="lt ${k}" cx="40" cy="${y}" r="7"/>`).join('')
  + '<ellipse class="puddle" cx="40" cy="89" rx="22" ry="2.6"/></svg>';
/** Green when the GPU idles, amber when it is busy, red under heavy load (or when it runs hot). */
function signal(svg, v, hot) {
  if (!svg) return;
  const k = hot || v >= 0.67 ? 'stop' : v >= 0.34 ? 'wait' : 'go';
  if (svg.dataset.k !== k) svg.dataset.k = k;
}

// ---------------------------------------------------------------- rain gauge (memory)
const GAUGE_SVG = '<svg class="raingauge" viewBox="0 0 80 92" aria-hidden="true">'
  // every gauge on a page shares this id; the shape is the same in each, so whichever one the url finds is right
  + '<clipPath id="pbRainTube"><rect x="27" y="16" width="26" height="70" rx="3"/></clipPath>'
  + '<rect class="glass" x="27" y="16" width="26" height="70" rx="3"/>'
  + '<g clip-path="url(#pbRainTube)"><g class="lvl" style="transform:translateY(70px)"><rect class="water" x="20" y="16" width="40" height="80"/>'
  + '<g class="wv"><path d="M2 16Q8 14 14 16T26 16T38 16T50 16T62 16T74 16T86 16"/></g></g></g>'
  + '<rect class="rim" x="27" y="16" width="26" height="70" rx="3"/>'
  + Array.from({ length: 7 }, (_, i) => `<path class="tk" d="M53 ${26 + i * 10}h${i % 2 ? 4 : 7}"/>`).join('')
  + '<path class="funnel" d="M21 14H59L53 18H27Z"/><g class="fall"><path d="M40 0q3.4 5 0 8.4q-3.4-3.4 0-8.4z"/></g></svg>';
/** Fills the gauge to v (0–1). */
function gauge(svg, v, hot) {
  const l = svg?.querySelector('.lvl');
  if (!l) return;
  l.style.transform = `translateY(${f1(70 - clamp01(v) * 68)}px)`;
  svg.classList.toggle('hot', !!hot);
}

// ---------------------------------------------------------------- skyline (network)
// the last readings as a row of towers, the newest on the right with an aircraft light; windows are a pattern, so
// a tower only changes its height
const BARS = 16, BW = 80 / BARS;
const SKYLINE_SVG = (() => {
  let s = '<svg class="skyline" viewBox="0 0 80 92" preserveAspectRatio="none" aria-hidden="true">'
    + '<pattern id="pbRainWin" width="5" height="6" patternUnits="userSpaceOnUse"><rect x="1.4" y="1.5" width="1.6" height="2" class="pw"/></pattern>';
  for (let i = 0; i < BARS; i++) {
    const x = f1(i * BW + 0.4), w = f1(BW - 0.8);
    s += `<g class="b"><rect class="bd" x="${x}" y="86" width="${w}" height="6"/><rect class="bw" x="${x}" y="86" width="${w}" height="6"/></g>`;
  }
  return s + '<path class="mast" d="M77.5 80V74"/><circle class="beacon" cx="77.5" cy="74" r="1.6"/></svg>';
})();
function skyline(svg, hist) {
  if (!svg) return;
  const bars = svg.querySelectorAll('.b');
  const pad = BARS - hist.length;
  bars.forEach((b, i) => {
    const v = i < pad ? 0 : hist[i - pad], hh = 6 + clamp01(v) * 70, y = f1(92 - hh);
    for (const r of b.children) { r.setAttribute('y', y); r.setAttribute('height', f1(hh)); }
  });
  const top = 92 - (6 + clamp01(hist[hist.length - 1]) * 70);
  svg.querySelector('.mast')?.setAttribute('d', `M77.5 ${f1(top)}V${f1(top - 6)}`);
  svg.querySelector('.beacon')?.setAttribute('cy', f1(top - 6));
}

/**
 * The drawing that stands for a metric: a traffic light for the GPU, a rain gauge for memory, a skyline of the recent
 * readings for the network, a tower block lighting its windows for everything else.
 * set(holder, v, hot) shows reading v (0–1) in the holder element the markup was put in.
 */
export function rainyArt(metric) {
  if (metric === 'gpu' || metric === 'gpupower') return { html: SIGNAL_SVG, set: (holder, v, hot) => signal(holder.firstElementChild, v, hot) };
  if (metric === 'ram' || metric === 'vram') return { html: GAUGE_SVG, set: (holder, v, hot) => gauge(holder.firstElementChild, v, hot) };
  if (metric === 'net' || metric === 'down' || metric === 'up' || metric === 'ping') {
    // each drawing keeps its own short history: set() is the only thing it is told
    const hist = [];
    return {
      html: SKYLINE_SVG,
      set: (holder, v) => { hist.push(clamp01(v)); if (hist.length > BARS) hist.shift(); skyline(holder.firstElementChild, hist); },
    };
  }
  return { html: TOWER_SVG, set: (holder, v, hot) => tower(holder.firstElementChild, v, hot) };
}
