import { SKINS } from '../registry.js';
import { INK_COLON, inkDigits, paperArt } from './paper.js';

// Paper sketch: the clock written in pen, gauges and metric tiles as doodles (see paper.js).
SKINS.paper = { clockClass: 'inked', digits: inkDigits, colon: INK_COLON, art: paperArt };
