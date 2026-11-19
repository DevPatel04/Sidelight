// ---------------------------------------------------------------- Cockpit theme: instruments
// The Cockpit theme draws some widgets as aircraft instruments: the clock on a seven-segment LCD, gauges as needle
// dials. Widgets ask cockpit() when they are created; the theme is part of the layout key, so switching themes
// builds them again. Everything here is drawn from numbers only, never from agent text.

export const cockpit = () => document.documentElement.dataset.theme === 'cockpit';

// seven-segment digits: segment end points in a 60 × 104 cell, and which segments each digit lights
const SEG = { a: [12, 8, 48, 8], b: [54, 13, 54, 47], c: [54, 57, 54, 91], d: [12, 96, 48, 96], e: [6, 57, 6, 91], f: [6, 13, 6, 47], g: [12, 52, 48, 52] };
const DIG = ['abcdef', 'bc', 'abged', 'abgcd', 'fgbc', 'afgcd', 'afgedc', 'abc', 'abcdefg', 'abcdfg'];
const f1 = (v) => v.toFixed(1);

// one segment as a long hexagon between two points
function segment(x1, y1, x2, y2, w) {
  const len = Math.hypot(x2 - x1, y2 - y1), ux = (x2 - x1) / len, uy = (y2 - y1) / len, nx = -uy, ny = ux, k = w / 2;
  return [[x1, y1], [x1 + ux * k + nx * k, y1 + uy * k + ny * k], [x2 - ux * k + nx * k, y2 - uy * k + ny * k], [x2, y2],
    [x2 - ux * k - nx * k, y2 - uy * k - ny * k], [x1 + ux * k - nx * k, y1 + uy * k - ny * k]].map((p) => f1(p[0]) + ',' + f1(p[1])).join(' ');
}
const DIGIT_SVG = DIG.map((lit) => {
  let s = '<svg class="lcd7" viewBox="-2 0 64 104" aria-hidden="true"><g transform="skewX(-7) translate(10 0)">';
  for (const k of 'abcdefg') s += `<polygon points="${segment(...SEG[k], 9)}" class="${lit.includes(k) ? 'on' : 'off'}"/>`;
  return s + '</g></svg>';
});

/** Digits as LCD svg markup; anything that is not a digit (a blank hour, "--") shows all segments dark. */
export const lcdDigits = (text) => [...String(text)].map((c) => (c >= '0' && c <= '9' ? DIGIT_SVG[+c] : DIGIT_SVG[8].replaceAll('"on"', '"off"'))).join('');

// a dial from 7:30 round to 4:30 (270°), with green, amber and red bands like an engine gauge
const A0 = 0.75 * Math.PI, SWEEP = 1.5 * Math.PI;
const angle = (v) => A0 + SWEEP * Math.max(0, Math.min(1, v));

/** Svg markup of an empty dial; set its needle with needle(). warn and hot are where amber and red start (0–1). */
export function dialSvg(warn = 0.7, hot = 0.85) {
  const cx = 50, cy = 50, r = 42;
  const arc = (from, to, rr) => {
    const [a, b] = [angle(from), angle(to)];
    return `M${f1(cx + Math.cos(a) * rr)} ${f1(cy + Math.sin(a) * rr)} A${rr} ${rr} 0 ${b - a > Math.PI ? 1 : 0} 1 ${f1(cx + Math.cos(b) * rr)} ${f1(cy + Math.sin(b) * rr)}`;
  };
  let s = `<svg class="dial" viewBox="0 0 100 100" aria-hidden="true"><circle class="bezel" cx="${cx}" cy="${cy}" r="${r + 5}"/>`;
  s += `<path class="band ok" d="${arc(0, warn, r - 3)}"/><path class="band warn" d="${arc(warn, hot, r - 3)}"/><path class="band hot" d="${arc(hot, 1, r - 3)}"/>`;
  for (let i = 0; i <= 10; i++) {
    const a = angle(i / 10), r1 = r - (i % 5 ? 9 : 13);
    s += `<path class="tick${i % 5 ? '' : ' major'}" d="M${f1(cx + Math.cos(a) * (r - 6))} ${f1(cy + Math.sin(a) * (r - 6))} L${f1(cx + Math.cos(a) * r1)} ${f1(cy + Math.sin(a) * r1)}"/>`;
  }
  // the needle points right (0°) and is turned by a CSS transform, so it sweeps smoothly between readings
  s += `<g class="needle" style="transform:rotate(${f1((A0 * 180) / Math.PI)}deg)"><path d="M${cx - 9} ${cy} L${cx + r - 9} ${cy - 1.6} L${cx + r - 5} ${cy} L${cx + r - 9} ${cy + 1.6}Z"/></g>`;
  return s + `<circle class="hub" cx="${cx}" cy="${cy}" r="5"/></svg>`;
}

/** Points a dial's needle at v (0–1). */
export function needle(svg, v) {
  const n = svg?.querySelector('.needle');
  if (n) n.style.transform = `rotate(${f1((angle(v) * 180) / Math.PI)}deg)`;
}
