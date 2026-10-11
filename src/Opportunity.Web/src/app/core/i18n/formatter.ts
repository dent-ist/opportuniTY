import type { DateStyle } from '../preferences/ui-preferences';

/**
 * The one place that turns dates, times and numbers into display text (ADR-018 §11, Q-28, E15-T05). Everything
 * user-visible goes through a `Formatter`: components get it from `DisplayFormat` (user locale, date style and
 * display time zone as signals); pure model code receives one as an argument. Intl formatters are built once per
 * formatter and reused.
 *
 * - Instants (API timestamps are UTC) render in the display time zone; date-and-time and time values carry a zone
 *   indicator ("03/14/2026, 10:42 AM EDT").
 * - Calendar dates (`YYYY-MM-DD`, no time) never shift with the zone.
 * - Date order follows the locale (en-US MM/DD/YYYY, en-GB DD/MM/YYYY) or ISO 8601 when the user chose it.
 */
export interface FormatSettings {
  readonly locale: string;
  /** IANA zone id; an unknown id falls back to UTC so the zone shown stays explicit. */
  readonly timeZone: string;
  readonly dateStyle: DateStyle;
}

/** The display locale before the user's preference is known (Q-28). */
export const DEFAULT_LOCALE = 'en-US';

/** Shown for a missing value. */
export const NO_VALUE = '—';

export type Instant = string | number | Date;

export class Formatter {
  readonly locale: string;
  readonly timeZone: string;
  readonly iso: boolean;
  private readonly cache = new Map<string, Intl.DateTimeFormat | Intl.NumberFormat>();

  constructor(settings: FormatSettings) {
    this.locale = settings.locale;
    this.timeZone = validZone(settings.timeZone);
    this.iso = settings.dateStyle === 'iso';
  }

  /** Date and time in the display time zone with a zone indicator; `seconds` for audit-grade stamps. */
  dateTime(value: Instant | null | undefined, options: { seconds?: boolean } = {}): string {
    const date = toDate(value);
    if (!date) return missing(value);
    const key = options.seconds ? 'dts' : 'dt';
    const f = this.dtf(key, {
      ...DATE_PARTS,
      ...TIME_PARTS,
      ...(options.seconds ? { second: '2-digit' } : {}),
      timeZoneName: 'short',
    });
    if (!this.iso) return f.format(date);
    const p = parts(f, date);
    const time = options.seconds ? `${p.hour}:${p.minute}:${p.second}` : `${p.hour}:${p.minute}`;
    return `${p.year}-${p.month}-${p.day} ${time} ${p.timeZoneName}`;
  }

  /** The calendar date of an instant in the display time zone. */
  date(value: Instant | null | undefined): string {
    const date = toDate(value);
    if (!date) return missing(value);
    return this.formatDate('d', date, this.timeZone);
  }

  /** The time of an instant in the display time zone, with a zone indicator ("10:42 AM EDT"). */
  time(value: Instant | null | undefined): string {
    const date = toDate(value);
    if (!date) return missing(value);
    const f = this.dtf('t', { ...TIME_PARTS, timeZoneName: 'short' });
    if (!this.iso) return f.format(date);
    const p = parts(f, date);
    return `${p.hour}:${p.minute} ${p.timeZoneName}`;
  }

  /** A calendar date without time (`2026-03-14`): the same day in every zone. */
  calendarDate(value: string | null | undefined): string {
    if (!value) return NO_VALUE;
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
    if (!m) return this.date(value);
    return this.formatDate('cd', new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])), 'UTC');
  }

  number(value: number | string | null | undefined, options?: Intl.NumberFormatOptions): string {
    if (value === null || value === undefined || value === '') return NO_VALUE;
    const n = Number(value);
    if (!Number.isFinite(n)) return String(value);
    return this.nf(options).format(n);
  }

  /** A fraction (0.42) as a percentage ("42%"). */
  percent(fraction: number, maximumFractionDigits = 0): string {
    return this.number(fraction, { style: 'percent', maximumFractionDigits });
  }

  /** Large counts abbreviated ("1.2M"). */
  compact(value: number): string {
    return this.number(value, { notation: 'compact', maximumFractionDigits: 1 });
  }

  /** A size in bytes ("512 B", "1.4 MB"; 1024-based like file managers on the review desktop). */
  bytes(value: number | string | null | undefined): string {
    if (value === null || value === undefined || value === '') return NO_VALUE;
    const bytes = Number(value);
    if (!Number.isFinite(bytes)) return String(value);
    if (Math.abs(bytes) < 1024) return `${this.number(bytes)} B`;
    let v = bytes / 1024;
    let unit = 0;
    while (Math.abs(v) >= 1024 && unit < BYTE_UNITS.length - 1) {
      v /= 1024;
      unit++;
    }
    return `${this.number(v, { maximumFractionDigits: 1 })} ${BYTE_UNITS[unit]}`;
  }

  private formatDate(key: string, date: Date, timeZone: string): string {
    const f = this.dtf(key, { ...DATE_PARTS, timeZone });
    if (!this.iso) return f.format(date);
    const p = parts(f, date);
    return `${p.year}-${p.month}-${p.day}`;
  }

  private dtf(key: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
    let f = this.cache.get(key) as Intl.DateTimeFormat | undefined;
    if (!f) {
      f = new Intl.DateTimeFormat(this.locale, {
        timeZone: this.timeZone,
        ...options,
        ...(this.iso ? { hourCycle: 'h23' } : {}),
      });
      this.cache.set(key, f);
    }
    return f;
  }

  private nf(options?: Intl.NumberFormatOptions): Intl.NumberFormat {
    const key = options ? `n${JSON.stringify(options)}` : 'n';
    let f = this.cache.get(key) as Intl.NumberFormat | undefined;
    if (!f) {
      f = new Intl.NumberFormat(this.locale, options);
      this.cache.set(key, f);
    }
    return f;
  }
}

const DATE_PARTS: Intl.DateTimeFormatOptions = {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
};
const TIME_PARTS: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' };
const BYTE_UNITS = ['KB', 'MB', 'GB', 'TB'];

const formatters = new Map<string, Formatter>();

/** A shared formatter for pure model code and tests; cached per settings. */
export function formatterFor(
  locale: string,
  timeZone = 'UTC',
  dateStyle: DateStyle = 'locale',
): Formatter {
  const key = `${locale}|${timeZone}|${dateStyle}`;
  let f = formatters.get(key);
  if (!f) {
    f = new Formatter({ locale, timeZone, dateStyle });
    formatters.set(key, f);
  }
  return f;
}

/** `value` in `locale`'s number format (pure helper for model code that has no zone to care about). */
export function formatNumber(
  value: number | string | null | undefined,
  locale: string,
  options?: Intl.NumberFormatOptions,
): string {
  return formatterFor(locale).number(value, options);
}

/** The browser's IANA time zone (UTC when it cannot be determined). */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** `zone` when this browser knows it, otherwise UTC. */
export function validZone(zone: string | null | undefined): string {
  if (!zone) return 'UTC';
  try {
    new Intl.DateTimeFormat('en', { timeZone: zone });
    return zone;
  } catch {
    return 'UTC';
  }
}

function toDate(value: Instant | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** A missing value as "—"; an unparseable one as given, so nothing is silently hidden. */
function missing(value: Instant | null | undefined): string {
  return value === null || value === undefined || value === '' ? NO_VALUE : String(value);
}

function parts(f: Intl.DateTimeFormat, date: Date): Record<Intl.DateTimeFormatPartTypes, string> {
  const out = {} as Record<Intl.DateTimeFormatPartTypes, string>;
  for (const p of f.formatToParts(date)) out[p.type] = p.value;
  return out;
}
