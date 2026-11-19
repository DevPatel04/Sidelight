import { SKINS } from '../registry.js';
import { LAMPS, rainyArt, signDigits } from './rainy.js';

// Rainy city: the clock as a lit street sign with rain running down it and its reflection on the wet street, sodium
// lamps for the colon; a tower block, a traffic light, a rain gauge or a skyline for gauges (rainy.js).
SKINS.rainy = { clockClass: 'sign', digits: signDigits, colon: LAMPS, art: rainyArt };
