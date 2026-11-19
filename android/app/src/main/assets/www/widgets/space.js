// ---------------------------------------------------------------- Space theme drawings
// Wide chrome digits for the clock with the seconds as a moon orbiting a small planet, and the gauge pictures: a star
// that swells into a red giant (CPU, temperatures), a rocket whose flame grows (GPU), a moon phase (memory) and a
// satellite on an orbit for everything else. Everything here is drawn from numbers only.

const f1 = (n) => n.toFixed(1);
let uid = 0; // per-picture gradient ids: a gradient's stop colours resolve where it is defined, so tiles must not share one

// ---------------------------------------------------------------- clock digits
// Squared digits with rounded corners on a 72×104 box, stroked with one chrome gradient. Every digit keeps the full
// box, so a 1 is as wide as an 8 and the time does not jump about.
const L = 10, R = 62, T = 10, B = 94, M = 52, r = 12;
const tl = `Q${L} ${T} ${L + r} ${T}`, tr = `Q${R} ${T} ${R} ${T + r}`, br = `Q${R} ${B} ${R - r} ${B}`, bl = `Q${L} ${B} ${L} ${B - r}`;
const BOX = `M${L + r} ${T}H${R - r}${tr}V${B - r}${br}H${L + r}${bl}V${T + r}${tl}Z`;
const GLYPH = {
  0: BOX + `M${R - 8} ${T + 16}L${L + 8} ${B - 16}`,
  1: `M22 ${T + 12}L36 ${T}V${B}`,
  2: `M${L} ${T}H${R - r}${tr}V${M - r}Q${R} ${M} ${R - r} ${M}H${L + r}Q${L} ${M} ${L} ${M + r}V${B}H${R}`,
  3: `M${L} ${T}H${R - r}${tr}V${B - r}${br}H${L}M24 ${M}H${R}`,
  4: `M${L} ${T}V${M - r}Q${L} ${M} ${L + r} ${M}H${R}M${R} ${T}V${B}`,
  5: `M${R} ${T}H${L}V${M}H${R - r}Q${R} ${M} ${R} ${M + r}V${B - r}${br}H${L}`,
  6: `M${R} ${T}H${L + r}Q${L} ${T} ${L} ${T + r}V${B - r}Q${L} ${B} ${L + r} ${B}H${R - r}Q${R} ${B} ${R} ${B - r}V${M + r}Q${R} ${M} ${R - r} ${M}H${L}`,
  7: `M${L} ${T}H${R - r}${tr}V${B}`,
  8: BOX + `M${L} ${M}H${R}`,
  9: `M${L} ${B}H${R - r}Q${R} ${B} ${R} ${B - r}V${T + r}Q${R} ${T} ${R - r} ${T}H${L + r}Q${L} ${T} ${L} ${T + r}V${M - r}Q${L} ${M} ${L + r} ${M}H${R}`,
};
// the same definition in every digit, so whichever one the page finds first draws them all alike
const CHROME = '<defs><linearGradient id="spx-chrome" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="0" y2="104">'
  + '<stop offset=".18" stop-color="#ffffff"/><stop offset=".62" stop-color="#b9c6ff"/><stop offset="1" stop-color="#8a7dff"/></linearGradient></defs>';
const digit = (c) => `<svg class="spx7" viewBox="0 0 72 104" aria-hidden="true">${CHROME}<path class="on" d="${GLYPH[c] || ''}"/></svg>`;

// The seconds: a moon on a tilted orbit around a ringed planet. Two nested groups swing it left–right and up–down
// (CSS, eased like a sine), which traces the ellipse; the copy over the planet is clipped to the near half of the orbit,
// so the moon passes behind the planet and then in front of it. The phase is set from the clock when the minutes are
// drawn, once a minute, so the moon stays on the second without anything running every tick.
function orbit() {
  const now = new Date(), s = now.getSeconds() + now.getMilliseconds() / 1000;
  const dx = f1((s + 15) % 60), dy = f1(s % 60); // seconds into the 30 s swings: x starts at the left (45 s), y at the top (0 s)
  const id = 'spx-o' + ++uid; // its own ids: the copy in the hours is hidden, and a hidden gradient paints nothing
  const moon = `<g class="mx" style="animation-delay:-${dx}s"><g class="my" style="animation-delay:-${dy}s"><circle cx="32" cy="32" r="4.4"/></g></g>`;
  return '<svg class="orb" viewBox="0 0 64 64" aria-hidden="true"><defs>'
    + `<radialGradient id="${id}p" cx="35%" cy="30%"><stop offset="0" stop-color="#d9ccff"/><stop offset="1" stop-color="#3b2a8a"/></radialGradient>`
    + `<clipPath id="${id}n"><rect x="0" y="32" width="64" height="40"/></clipPath></defs>`
    + `<path class="ring" d="M4 32A28 11.8 0 0 1 60 32"/>${moon}<circle cx="32" cy="32" r="13" fill="url(#${id}p)"/>`
    + `<path class="ring near" d="M4 32A28 11.8 0 0 0 60 32"/><g clip-path="url(#${id}n)">${moon}</g></svg>`;
}

/** The clock's hours or minutes ("09"); each carries the orbit, which the sheet shows only after the minutes. */
export const spaceDigits = (text) => [...text].map(digit).join('') + orbit();

// the colon: two small four-pointed stars
const SPARK = 'M0 -5L1.2 -1.2L5 0L1.2 1.2L0 5L-1.2 1.2L-5 0L-1.2 -1.2Z';
export const SPACE_COLON = `<svg class="spx-colon" viewBox="0 0 16 104" aria-hidden="true"><path transform="translate(8 34)" d="${SPARK}"/><path transform="translate(8 74)" d="${SPARK}"/></svg>`;

// ---------------------------------------------------------------- gauge pictures (viewBox 80×92)
const RAYS = Array.from({ length: 12 }, (_, i) => {
  const a = (i * Math.PI) / 6, r1 = 22 + (i % 2) * 6;
  return `M${f1(Math.cos(a) * 16)} ${f1(Math.sin(a) * 16)}L${f1(Math.cos(a) * r1)} ${f1(Math.sin(a) * r1)}`;
}).join('');

// a star whose core grows with the reading; blue-white while cool, a swollen red giant when it runs hot
function star() {
  const id = 'spx-st' + ++uid;
  const html = `<svg class="spx-star" viewBox="0 0 80 92" aria-hidden="true"><defs><radialGradient id="${id}">`
    + '<stop offset="0" class="c0"/><stop offset=".5" class="c1"/><stop offset="1" class="c2"/></radialGradient></defs>'
    + `<circle class="glow" cx="40" cy="46" r="17" fill="url(#${id})"/>`
    + `<g transform="translate(40 46)"><g class="cor"><path class="rays" d="${RAYS}"/></g></g>`
    + `<circle class="core" cx="40" cy="46" r="9" fill="url(#${id})"/><circle class="in" cx="40" cy="46" r="5"/></svg>`;
  return {
    html,
    set(holder, v, hot) {
      const svg = holder.firstElementChild, rr = 9 + Math.max(0, Math.min(1, v)) * 16;
      svg.classList.toggle('hot', !!hot);
      svg.querySelector('.glow').style.r = f1(rr * 1.9);
      svg.querySelector('.core').style.r = f1(rr);
      svg.querySelector('.in').style.r = f1(rr * 0.55);
      svg.querySelector('.cor').style.transform = `scale(${(rr / 14).toFixed(2)})`;
    },
  };
}

// a rocket on a gentle bob; its engine flame gets longer with the load
const ROCKET = '<svg class="spx-rocket" viewBox="0 0 80 92" aria-hidden="true"><g class="rkt">'
  + '<g transform="translate(40 64)"><g class="flame"><g class="flk"><path class="f1" d="M-8 0Q0 48 8 0Z"/><path class="f2" d="M-4 0Q0 28 4 0Z"/></g></g></g>'
  + '<path class="hull" d="M40 6C51 17 53 36 50 62L30 62C27 36 29 17 40 6Z"/><path class="nose" d="M40 6C46 12 49 20 50 28L30 28C31 20 34 12 40 6Z"/>'
  + '<circle class="port" cx="40" cy="38" r="5"/><path class="fins" d="M30 46L21 64L30 61ZM50 46L59 64L50 61Z"/><path class="nozzle" d="M33 62H47L45 66H35Z"/></g></svg>';
const rocket = () => ({
  html: ROCKET,
  set(holder, v, hot) {
    const svg = holder.firstElementChild, p = Math.max(0, Math.min(1, v));
    svg.classList.toggle('hot', !!hot);
    svg.querySelector('.flame').style.transform = `scale(${(0.6 + p * 0.4).toFixed(2)}, ${(0.15 + p).toFixed(2)})`;
  },
});

// the moon: the lit part is the share in use, growing from a thin crescent to full
const MR = 27, MX = 40, MY = 44;
const litPath = (f) => {
  const rx = Math.abs(1 - 2 * f) * MR;
  return `M${MX} ${MY - MR}A${MR} ${MR} 0 0 1 ${MX} ${MY + MR}A${f1(rx)} ${MR} 0 0 ${f < 0.5 ? 0 : 1} ${MX} ${MY - MR}Z`;
};
const CRATERS = [[50, 36, 4], [36, 54, 3.2], [54, 56, 2.4], [30, 34, 2]].map(([x, y, s]) => `<circle class="cr" cx="${x}" cy="${y}" r="${s}"/>`).join('');
const moon = () => ({
  html: `<svg class="spx-moon" viewBox="0 0 80 88" aria-hidden="true"><circle class="halo" cx="${MX}" cy="${MY}" r="${MR + 6}"/>`
    + `<circle class="dark" cx="${MX}" cy="${MY}" r="${MR}"/><path class="lit" d="${litPath(0)}"/>${CRATERS}`
    + `<circle class="rim" cx="${MX}" cy="${MY}" r="${MR}"/></svg>`,
  set(holder, v, hot) {
    const svg = holder.firstElementChild;
    svg.classList.toggle('hot', !!hot);
    svg.querySelector('.lit').setAttribute('d', litPath(Math.max(0.02, Math.min(1, v))));
  },
});

// any other reading: a satellite carried round a planet's orbit, the orbit lit behind it
const sat = () => ({
  html: '<svg class="spx-sat" viewBox="0 0 80 92" aria-hidden="true">'
    + '<circle class="track" cx="40" cy="46" r="30"/><circle class="arc" cx="40" cy="46" r="30" pathLength="100" transform="rotate(-90 40 46)"/>'
    + '<circle class="planet" cx="40" cy="46" r="14"/><path class="band" d="M27 42Q40 38 53 42M26.5 50Q40 46 53.5 50"/>'
    + '<g transform="translate(40 46)"><g class="sv"><g transform="translate(0 -30)"><rect class="panel" x="-8" y="-2" width="16" height="4" rx="1"/>'
    + '<circle class="body" r="3.4"/></g></g></g></svg>',
  set(holder, v, hot) {
    const svg = holder.firstElementChild, p = Math.max(0, Math.min(1, v));
    svg.classList.toggle('hot', !!hot);
    svg.querySelector('.arc').style.strokeDashoffset = f1(100 - p * 100);
    svg.querySelector('.sv').style.transform = `rotate(${f1(p * 360)}deg)`;
  },
});

const STARS = new Set(['cpu', 'cputemp', 'gputemp', 'hotspot']);
/** A gauge or tile picture for a metric (see skins.js). */
export function spaceArt(metric) {
  if (STARS.has(metric)) return star();
  if (metric === 'gpu') return rocket();
  if (metric === 'ram' || metric === 'vram') return moon();
  return sat();
}
