import { readFileSync } from 'node:fs';
import { contrastRatio, parseDensity, parseThemes } from './token-parser.testing';

// ADR-018 §6: automated WCAG 2.2 AA contrast check for every theme (1.4.3 text 4.5:1, 1.4.11 non-text 3:1),
// and the density budget (≥ 30 compact grid rows at 1080p).
const tokens = readFileSync('src/styles/_tokens.scss', 'utf8');
const themes = parseThemes(tokens);

const surfaces = ['bg', 'surface', 'surface-raised', 'surface-sunken', 'hover', 'selected'];

/** [foreground, background, minimum ratio, why]. */
const pairs: [string, string, number, string][] = [
  ...surfaces.flatMap((s): [string, string, number, string][] => [
    ['text', s, 4.5, 'body text'],
    ['text-muted', s, 4.5, 'secondary text'],
  ]),
  ...['bg', 'surface', 'surface-raised'].flatMap((s): [string, string, number, string][] => [
    ['accent-text', s, 4.5, 'links and accent text'],
    ['border-strong', s, 3, 'input and secondary button boundary'],
    ['accent', s, 3, 'primary button and checked checkbox fill'],
    ['danger', s, 3, 'danger button fill'],
    ['focus', s, 3, 'focus indicator'],
    ['danger-text', s, 4.5, 'field error text'],
  ]),
  ...['operator', 'field', 'phrase', 'range'].map((kind): [string, string, number, string] => [
    `syntax-${kind}`,
    'surface',
    4.5,
    'query bar syntax highlighting',
  ]),
  // Term-hit highlights (E16-T12): colour plus an underline that stands out ≥ 3:1 on the fill and every surface.
  ...['search', 'amber', 'green', 'blue', 'violet', 'rose', 'teal'].flatMap(
    (hl): [string, string, number, string][] => [
      [`hl-${hl}-text`, `hl-${hl}-bg`, 4.5, `${hl} highlight text`],
      [`hl-${hl}-line`, `hl-${hl}-bg`, 3, `${hl} highlight underline on its fill`],
      ...['bg', 'surface', 'surface-sunken'].map((s): [string, string, number, string] => [
        `hl-${hl}-line`,
        s,
        3,
        `${hl} highlight underline and swatch outline`,
      ]),
    ],
  ),
  ['on-accent', 'accent', 4.5, 'primary button label'],
  ['on-accent', 'accent-hover', 4.5, 'primary button label (hover)'],
  ['on-danger', 'danger', 4.5, 'danger button label'],
  ['on-danger', 'danger-hover', 4.5, 'danger button label (hover)'],
  ['text-inverse', 'restricted-bg', 4.5, 'restricted badge'],
  ['restricted-text', 'restricted-bg', 4.5, 'restricted badge'],
  ...['info', 'success', 'warning', 'danger', 'privileged', 'aeo'].flatMap(
    (tone): [string, string, number, string][] => [
      [`${tone}-text`, `${tone}-bg`, 4.5, `${tone} badge text`],
      [`${tone}-border`, 'surface', 3, `${tone} badge/icon boundary`],
    ],
  ),
];

describe('design tokens', () => {
  it('defines light, dark and high-contrast themes with the same colour tokens', () => {
    expect(Object.keys(themes).sort()).toEqual(['dark', 'high-contrast', 'light']);
    const names = Object.keys(themes['light']).sort();
    expect(names.length).toBeGreaterThan(30);
    expect(Object.keys(themes['dark']).sort()).toEqual(names);
    expect(Object.keys(themes['high-contrast']).sort()).toEqual(names);
  });

  for (const [theme, colours] of Object.entries(themes)) {
    describe(`${theme} theme`, () => {
      for (const [fg, bg, min, why] of pairs) {
        it(`${fg} on ${bg} ≥ ${min}:1 (${why})`, () => {
          expect(colours[fg], `--opp-color-${fg}`).toBeDefined();
          expect(colours[bg], `--opp-color-${bg}`).toBeDefined();
          const ratio = contrastRatio(colours[fg], colours[bg]);
          expect(
            ratio,
            `${colours[fg]} on ${colours[bg]} = ${ratio.toFixed(2)}`,
          ).toBeGreaterThanOrEqual(min);
        });
      }
    });
  }

  it('keeps compact controls at the 24 px minimum target size (WCAG 2.5.8)', () => {
    const compact = parseDensity(tokens, 'compact');
    expect(compact['control-height']).toBeGreaterThanOrEqual(24);
    expect(compact['row-height']).toBeGreaterThanOrEqual(24);
  });

  it('fits at least 30 compact grid rows in a 1080p browser window', () => {
    // 1080 px screen minus ~125 px browser UI = 955 px viewport at 100 % zoom and 16 px root font. Shell
    // chrome budget (ADR-018 §6.4): header 40 + list toolbar 40 + grid header 28 + status bar 28 + freshness
    // banner 24 = 160 px.
    const viewport = 1080 - 125;
    const shellChrome = 160;
    const compact = parseDensity(tokens, 'compact');
    expect(Math.floor((viewport - shellChrome) / compact['row-height'])).toBeGreaterThanOrEqual(30);
  });
});
