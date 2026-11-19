import { SKINS } from '../registry.js';
import './skin-cockpit.js';
import './skin-gaming.js';
import './skin-nature.js';
import './skin-paper.js';
import './skin-rainy.js';
import './skin-space.js';
import './skin-zen.js';

// ---------------------------------------------------------------- theme skins
// A theme may draw some widgets its own way. Its skin-<theme>.js puts an object in SKINS[theme]; every field is
// optional, and what a skin leaves out keeps the normal look (restyled by the theme's sheet):
//   clockClass   a class for the clock (.clock.<clockClass>), for the theme's sheet to style the drawn digits
//   digits(text) svg markup for the clock's hours or minutes ("09"); colon: svg markup for the colon
//   art(metric)  a picture for a gauge and a metric tile: { html, set(holder, v, hot) } — html goes into holder once,
//                set() shows reading v (0–1) and whether it runs hot, every update; it should change attributes or
//                styles rather than rebuild, so CSS transitions and animations run on
// Widgets ask skin() when they are created; the theme is part of the panel's layout key, so a new theme builds them
// again. Skins draw from numbers only, never from agent text, so their markup may go in with innerHTML.
export const skin = () => SKINS[document.documentElement.dataset.theme] || null;
