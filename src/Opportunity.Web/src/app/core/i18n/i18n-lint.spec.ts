import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

// E15-T05, ADR-018 §11: user-visible dates, times and numbers are formatted only in src/app/core/i18n (Formatter /
// DisplayFormat), so every value follows the user's locale, date style and display time zone and a locale switch
// applies without a reload. Elsewhere no hand-made Intl formatter, no toLocale*String, no Angular locale pipes, no
// hard-coded locale and no timestamp interpolated as received from the API.
const FORMAT_DIR = 'src/app/core/i18n/';
const LOCALE_SOURCES = [FORMAT_DIR, 'src/app/core/preferences/'];

const files = readdirSync('src/app', { recursive: true, encoding: 'utf8' })
  .map((f) => join('src/app', f).replaceAll('\\', '/'))
  .filter((f) => /\.(html|ts)$/.test(f))
  .filter((f) => !f.includes('/generated/') && !/\.(spec|testing)\.ts$/.test(f));

const formatRules: [RegExp, string][] = [
  [
    /\bIntl\.(DateTimeFormat|NumberFormat|RelativeTimeFormat|PluralRules|ListFormat)\b/,
    'Intl formatter',
  ],
  [/\.toLocale(Date|Time)?String\(/, 'toLocaleString'],
  [
    /\b(DatePipe|DecimalPipe|PercentPipe|CurrencyPipe|LOCALE_ID|formatDate|formatNumber)\b.*@angular/,
    'Angular locale API',
  ],
  [/\b(LOCALE_ID)\b/, 'LOCALE_ID'],
  [/\{\{[^}]*\|\s*(date|number|percent|currency)\b(?!\w)/, 'Angular locale pipe'],
];
const localeLiteral = /['"`]en(-(US|GB))?['"`]/;
// `{{ job.createdAt }}`: an API timestamp shown raw, in UTC and ISO form.
const rawTimestamp = /\{\{\s*[\w.?!]*(At|Timestamp)\s*\}\}/;

function scan(rules: [RegExp, string][], exempt: (file: string) => boolean): string[] {
  const violations: string[] = [];
  for (const file of files.filter((f) => !exempt(f))) {
    readFileSync(file, 'utf8')
      .split('\n')
      .forEach((line, i) => {
        if (/^\s*(\/\/|\*|\/\*)/.test(line)) return;
        for (const [rule, what] of rules) {
          if (rule.test(line)) violations.push(`${file}:${i + 1} ${what}: ${line.trim()}`);
        }
      });
  }
  return violations;
}

describe('i18n lint', () => {
  it('scans the source tree', () => {
    expect(files.some((f) => f.startsWith(FORMAT_DIR))).toBe(true);
    expect(files.length).toBeGreaterThan(100);
  });

  it('formats dates and numbers only through core/i18n', () => {
    expect(scan(formatRules, (f) => f.startsWith(FORMAT_DIR))).toEqual([]);
  });

  it('has no hard-coded display locale', () => {
    expect(
      scan([[localeLiteral, 'locale literal']], (f) => LOCALE_SOURCES.some((d) => f.startsWith(d))),
    ).toEqual([]);
  });

  it('never shows an API timestamp unformatted', () => {
    expect(scan([[rawTimestamp, 'raw timestamp']], () => false)).toEqual([]);
  });
});
