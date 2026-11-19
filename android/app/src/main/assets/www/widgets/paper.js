// ---------------------------------------------------------------- Paper sketch theme: things drawn in pen
// The Paper sketch theme writes the clock by hand and draws gauges as doodles: a speedometer, a thermometer for the
// GPU and a coffee mug for memory (skin-paper.js). Everything here is drawn from numbers only, never from agent text.

const f1 = (v) => v.toFixed(1);
const clamp01 = (v) => Math.max(0, Math.min(1, v || 0));

// One hidden <svg> holds what many drawings share: a "wobble" that bends straight lines into hand-drawn ones, and
// pencil hatching. Ids in the page are global, so it is made once, the first time the theme draws anything.
const NS = 'http://www.w3.org/2000/svg';
const DEFS = '<defs>'
  + '<filter id="pbWob" x="-5%" y="-5%" width="110%" height="110%"><feTurbulence type="fractalNoise" baseFrequency=".045" numOctaves="2" seed="4" result="n"/>'
  + '<feDisplacementMap in="SourceGraphic" in2="n" scale="2.4" xChannelSelector="R" yChannelSelector="G"/></filter>'
  + '<pattern id="pbHatch" width="4.5" height="4.5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><line x1="0" y1="0" x2="0" y2="4.5" stroke="#2b2f4a" stroke-width="1.2"/></pattern>'
  + '<pattern id="pbHatchRed" width="4.5" height="4.5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><line x1="0" y1="0" x2="0" y2="4.5" stroke="#b8322a" stroke-width="1.3"/></pattern>'
  + '</defs>';
export function paperDefs() {
  if (document.getElementById('pbPaperDefs')) return;
  const svg = document.createElementNS(NS, 'svg');
  svg.id = 'pbPaperDefs';
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('width', '0');
  svg.setAttribute('height', '0');
  svg.style.cssText = 'position:absolute;width:0;height:0;overflow:hidden';
  svg.innerHTML = DEFS; // a constant
  document.body.append(svg);
}

// digits written in one pen stroke each, in a 60 × 100 cell, the way a hand writes them (open loops, a crossed 7)
const STROKES = [
  'M32 9 C14 8 8 34 9 54 C10 80 20 93 32 92 C48 91 53 66 52 46 C51 22 44 9 30 10 C27 10 25 11 23 13',
  'M15 27 C24 22 31 15 37 8 C36 36 35 64 34 92',
  'M11 29 C13 12 30 6 41 10 C54 15 51 34 41 47 C31 60 19 74 10 90 C24 88 38 89 53 88',
  'M12 18 C24 5 48 8 47 27 C46 41 33 47 24 48 C40 47 54 58 51 75 C48 92 24 97 10 85',
  'M41 92 C41 64 42 36 43 8 C32 26 20 46 8 66 C24 65 40 64 55 64',
  'M49 10 C38 11 28 11 19 10 C17 22 16 34 14 46 C26 37 47 38 51 60 C54 84 30 98 10 86',
  'M47 13 C34 3 12 18 10 52 C9 80 21 93 33 92 C49 91 53 70 46 59 C38 47 17 49 11 65',
  'M8 13 C22 11 38 11 52 10 C40 36 30 62 24 92 M17 52 C26 51 35 50 44 50',
  'M31 48 C13 42 13 11 31 10 C49 9 49 41 31 48 C10 55 10 92 31 92 C53 92 53 55 31 48',
  'M48 33 C46 11 15 8 12 29 C10 49 40 55 48 33 C51 52 48 74 34 92',
];
// each digit is gone over twice, as a pen does, the second pass a little off and fainter
const DIGIT_SVG = STROKES.map((d, i) => '<svg class="ink7" viewBox="0 0 60 100" aria-hidden="true">'
  + `<path class="re" d="${d}" transform="translate(${i % 2 ? 1.6 : -1.2} ${i % 3 ? 1.2 : -1})"/><path class="st" d="${d}"/></svg>`);
// a blank or "--" hour: a short dash per place
const BLANK_SVG = '<svg class="ink7" viewBox="0 0 60 100" aria-hidden="true"><path class="st" d="M14 52 C26 49 36 51 47 49"/></svg>';

/** Handwritten digits; anything that is not a digit is a dash. */
export const inkDigits = (text) => { paperDefs(); return [...String(text)].map((c) => (c >= '0' && c <= '9' ? DIGIT_SVG[+c] : BLANK_SVG)).join(''); };

/** Two scribbled dots for the clock's colon. */
export const INK_COLON = '<svg class="ink-colon" viewBox="0 0 16 100" aria-hidden="true"><path d="M7 34 c3 -2 5 2 2 4 c-3 1 -4 -3 -1 -4"/><path d="M8 68 c3 -2 5 2 2 4 c-3 1 -4 -3 -1 -4"/></svg>';

// ---------------------------------------------------------------- doodled gauges (all on an 80 × 84 sheet)
// a half-circle speedometer: hatched up to the reading, a red pen needle; "z" for idle and "!!" for busy need no
// translation
const SC = 40, SY = 54, SR = 30;
const wedge = (v) => {
  const a = Math.PI + Math.PI * Math.max(0.005, clamp01(v));
  return `M${SC} ${SY} L${SC - SR} ${SY} A${SR} ${SR} 0 0 1 ${f1(SC + Math.cos(a) * SR)} ${f1(SY + Math.sin(a) * SR)}Z`;
};
const SPEEDO = '<svg class="pp speedo" viewBox="0 0 80 84" aria-hidden="true">'
  + `<path class="hatch" d="${wedge(0)}"/>`
  + `<g class="ln" filter="url(#pbWob)"><path d="M${SC - SR} ${SY} A${SR} ${SR} 0 0 1 ${SC + SR} ${SY}"/><path d="M${SC - SR - 4} ${SY} H${SC + SR + 4}"/>`
  + [0.25, 0.5, 0.75].map((k) => { const a = Math.PI + Math.PI * k; return `<path d="M${f1(SC + Math.cos(a) * (SR - 5))} ${f1(SY + Math.sin(a) * (SR - 5))} L${f1(SC + Math.cos(a) * (SR + 1))} ${f1(SY + Math.sin(a) * (SR + 1))}"/>`; }).join('')
  + '</g>'
  // the needle points right and is turned by a CSS transform, so it swings between readings
  + `<g class="needle" style="transform:rotate(180deg)"><path d="M${SC} ${SY} L${SC + SR + 2} ${SY - 1}"/></g><circle class="hub" cx="${SC}" cy="${SY}" r="3.2"/>`
  + '<text class="lo" x="6" y="72">z</text><text class="hi" x="62" y="72">!!</text></svg>';

// a thermometer: the red column rises with the reading
const THERMO = '<svg class="pp thermo" viewBox="0 0 80 84" aria-hidden="true">'
  + '<rect class="col" x="37" y="12" width="6" height="58" style="transform:scaleY(.05)"/><circle class="bulb" cx="40" cy="70" r="7.5"/>'
  + '<g class="ln" filter="url(#pbWob)"><path d="M34 12 a6 6 0 0 1 12 0 V61 a10 10 0 1 1 -12 0Z"/>'
  + [0, 1, 2, 3, 4].map((i) => `<path d="M49 ${16 + i * 11} h${i % 2 ? 4 : 7}"/>`).join('') + '</g>'
  + '<text class="hi" x="56" y="20">!!</text></svg>';

// a coffee mug that fills with the reading, steam rising. Every mug shares this clip id; the shape is the same in
// each, so whichever one the url finds is right.
const MUG_IN = 'M18 30 H58 V70 Q58 78 50 78 H26 Q18 78 18 70Z';
const MUG = '<svg class="pp mug" viewBox="0 0 80 84" aria-hidden="true">'
  + `<clipPath id="pbMugClip"><path d="${MUG_IN}"/></clipPath>`
  + '<g clip-path="url(#pbMugClip)"><g class="coffee" style="transform:translateY(48px)"><rect x="18" y="30" width="40" height="50"/><path class="crema" d="M18 31 H58"/></g></g>'
  + `<g class="ln" filter="url(#pbWob)"><path d="${MUG_IN}"/><path d="M58 40 Q70 40 70 52 Q70 62 58 62"/></g>`
  + '<g class="steam"><path d="M28 24 q-4 -5 0 -9 t0 -9"/><path d="M38 24 q-4 -5 0 -9 t0 -9"/><path d="M48 24 q-4 -5 0 -9 t0 -9"/></g></svg>';

/**
 * The doodle that stands for a metric: a thermometer for the GPU, a coffee mug for memory, a speedometer for the rest.
 * set(holder, v, hot) shows reading v (0–1) by moving parts in place, so CSS transitions run.
 */
export function paperArt(metric) {
  paperDefs();
  const hotFlag = (svg, hot) => svg.classList.toggle('hot', !!hot);
  if (metric === 'gpu') {
    return { html: THERMO, set(holder, v, hot) {
      const svg = holder.firstElementChild, col = svg?.querySelector('.col');
      if (!col) return;
      col.style.transform = `scaleY(${f1(0.05 + clamp01(v) * 0.95)})`;
      hotFlag(svg, hot);
    } };
  }
  if (metric === 'ram' || metric === 'vram') {
    return { html: MUG, set(holder, v, hot) {
      const svg = holder.firstElementChild, c = svg?.querySelector('.coffee');
      if (!c) return;
      c.style.transform = `translateY(${f1(48 - clamp01(v) * 42)}px)`;
      hotFlag(svg, hot);
    } };
  }
  return { html: SPEEDO, set(holder, v, hot) {
    const svg = holder.firstElementChild, n = svg?.querySelector('.needle');
    if (!n) return;
    n.style.transform = `rotate(${f1(180 + 180 * clamp01(v))}deg)`;
    svg.querySelector('.hatch').setAttribute('d', wedge(v));
    hotFlag(svg, hot);
  } };
}

// The sheet also bends charts and rings with the wobble, on pages that have no doodle to create the defs: make them
// as soon as the page has a body. They are a few bytes and draw nothing, so other themes do not mind them.
if (document.body) paperDefs(); else document.addEventListener('DOMContentLoaded', paperDefs, { once: true });
