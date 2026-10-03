import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

// ADR-018 §6.1: styles consume tokens only. Literal colours are allowed in src/styles/_tokens.scss alone;
// CSS system colours (Canvas, CanvasText, Highlight, …) are allowed for forced-colors mode.
const TOKEN_FILE = 'src/styles/_tokens.scss';

const files = readdirSync('src', { recursive: true, encoding: 'utf8' })
  .map((f) => join('src', f).replaceAll('\\', '/'))
  .filter((f) => /\.(scss|css|html|ts)$/.test(f))
  .filter(
    (f) => f !== TOKEN_FILE && !f.includes('/generated/') && !/\.(spec|testing)\.ts$/.test(f),
  );

const NAMED =
  'white|black|red|green|blue|gray|grey|silver|yellow|orange|purple|pink|navy|teal|maroon|olive|lime|aqua|fuchsia|brown';
const styleRules: [RegExp, string][] = [
  [/(?<![\w&-])#[0-9a-f]{3,8}\b/i, 'hex colour'],
  [/\b(rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\(/i, 'colour function'],
  [new RegExp(`:[^;{}]*\\b(${NAMED})\\b[^;{}]*;`, 'i'), 'named colour'],
];

function stripComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/.*$/gm, '$1');
}

describe('design lint', () => {
  it('scans the source tree', () => {
    expect(files.some((f) => f.endsWith('.scss'))).toBe(true);
  });

  it('has no hard-coded colours outside the token file', () => {
    const violations: string[] = [];
    for (const file of files.filter((f) => /\.(scss|css)$/.test(f))) {
      stripComments(readFileSync(file, 'utf8'))
        .split('\n')
        .forEach((line, i) => {
          for (const [rule, what] of styleRules) {
            if (rule.test(line)) violations.push(`${file}:${i + 1} ${what}: ${line.trim()}`);
          }
        });
    }
    expect(violations).toEqual([]);
  });

  it('keeps component styles in stylesheet files (no inline styles to bypass the lint)', () => {
    const violations: string[] = [];
    for (const file of files) {
      const text = readFileSync(file, 'utf8');
      if (file.endsWith('.ts') && /\bstyles\s*:/.test(text))
        violations.push(`${file}: inline styles`);
      if (file.endsWith('.html') && /\sstyle\s*=/.test(text))
        violations.push(`${file}: style attribute`);
      if (/\btemplate\s*:[\s\S]*\sstyle\s*=/.test(text))
        violations.push(`${file}: style attribute`);
    }
    expect(violations).toEqual([]);
  });
});
