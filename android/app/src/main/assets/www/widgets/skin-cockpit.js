import { SKINS } from '../registry.js';
import { dialSvg, lcdDigits, needle } from './instruments.js';

// Cockpit: the clock on a seven-segment LCD, gauges and metric tiles as needle dials (see instruments.js).
SKINS.cockpit = {
  clockClass: 'lcd',
  digits: lcdDigits,
  art: () => ({ html: dialSvg(0.7, 0.85), set: (holder, v) => needle(holder.firstElementChild, v) }),
};
