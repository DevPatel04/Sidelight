import { SKINS } from '../registry.js';
import { BERRIES, leafDigits, natureArt } from './nature.js';

// Nature: the clock grown from leaves with berries for the colon; a tree, a campfire or a pond for gauges (nature.js).
SKINS.nature = { clockClass: 'leafy', digits: leafDigits, colon: BERRIES, art: natureArt };
