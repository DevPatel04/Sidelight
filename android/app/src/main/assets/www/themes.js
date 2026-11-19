// ---------------------------------------------------------------- the panel's themes
// One place that lists them: the theme a config may name, and the background "the theme's own" means for it.
// Each theme has a stylesheet (theme-<id>.css, linked from index.html), and may have a skin that draws some widgets
// its own way (widgets/skin-<id>.js, see widgets/skins.js) and a background of its own (bg-<id>.js or background.js).
export const THEMES = Object.freeze({
  default: { bg: 'glow' },
  amoled: { bg: 'glow' },
  midnight: { bg: 'glow' },
  warm: { bg: 'glow' },
  graphite: { bg: 'glow' },
  glass: { bg: 'blobs' },
  cockpit: { bg: 'horizon' },
  nature: { bg: 'forest' },
  space: { bg: 'galaxy' },
  gaming: { bg: 'synthwave' },
  paper: { bg: 'notebook' },
  zen: { bg: 'inkwash' },
  rainy: { bg: 'city' },
});

/** The theme a config names, or "default" for one the panel does not have. */
export const themeOf = (name) => (Object.prototype.hasOwnProperty.call(THEMES, name) ? name : 'default');
