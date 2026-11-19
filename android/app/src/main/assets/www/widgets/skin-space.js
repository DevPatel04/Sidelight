import { SKINS } from '../registry.js';
import { SPACE_COLON, spaceArt, spaceDigits } from './space.js';

// Space: chrome digits with a moon orbiting a planet for the seconds; gauges as a star, a rocket, a moon or a
// satellite (space.js).
SKINS.space = { clockClass: 'cosmo', digits: spaceDigits, colon: SPACE_COLON, art: spaceArt };
