import type { SearchFreshness, TotalCount } from '../../../core/api/generated/models';
import { toServedFreshness } from '../../../core/search/search-freshness';

/**
 * Count label of the results header (Q-10, Q-32, familiarity guide §6): exact counts with thousands separators;
 * "≈" while the served index is not known to be current; a capped count as "≥ 10,000 (approx.)"; approximate
 * counts of a million or more abbreviated ("~1.2M"). Never a bare capped number.
 */
export function countLabel(total: TotalCount, freshness: SearchFreshness, locale: string): string {
  const value = Number(total.value);
  const n = new Intl.NumberFormat(locale).format(value);
  if (total.relation === 'gte') return `≥ ${n} (approx.)`;
  if (freshness.current === true) return n;
  if (value >= 1_000_000) {
    return `~${new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(value)}`;
  }
  return `≈ ${n}`;
}

/** Whether counts are shown as approximate (the "≈" of `countLabel`). */
export function isApproximate(total: TotalCount, freshness: SearchFreshness): boolean {
  return total.relation === 'gte' || freshness.current !== true;
}

/**
 * The stamp of a result set (Q-10, E16-T07): when the index it was served from was current, in plain words —
 * "Results current as of 10:42", or "Results as of 10:42" while changes were still pending (the footnote then says
 * how many). Reviewers never see generation numbers; admins find them in the freshness detail.
 */
export function freshnessLabel(
  freshness: SearchFreshness,
  locale: string,
  timeZone: string,
): string {
  const time = formatTime(freshness.asOf as string, locale, timeZone);
  return toServedFreshness(freshness).state === 'current'
    ? `Results current as of ${time}`
    : `Results as of ${time}`;
}

/** "10:42" in the workspace display time zone ("—" for a missing or invalid time). */
export function formatTime(iso: string, locale: string, timeZone: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  try {
    return new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit', timeZone }).format(
      date,
    );
  } catch {
    return new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(date);
  }
}

/** Cell formatters for one grid: built once (Intl formatters are expensive), used for every rendered cell. */
export class CellFormatter {
  private readonly date: Intl.DateTimeFormat;
  private readonly number: Intl.NumberFormat;
  private readonly size: Intl.NumberFormat;

  constructor(locale: string, timeZone: string) {
    const options: Intl.DateTimeFormatOptions = {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      timeZoneName: 'short',
    };
    try {
      this.date = new Intl.DateTimeFormat(locale, { ...options, timeZone });
    } catch {
      // An unknown zone id: fall back to UTC rather than the browser's zone, so the zone shown stays explicit.
      this.date = new Intl.DateTimeFormat(locale, { ...options, timeZone: 'UTC' });
    }
    this.number = new Intl.NumberFormat(locale);
    this.size = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  }

  /** Date and time in the workspace display time zone with a zone indicator (Q-28). */
  formatDate(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : this.date.format(date);
  }

  formatNumber(value: number): string {
    return this.number.format(value);
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${this.number.format(bytes)} B`;
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${this.size.format(value)} ${units[unit]}`;
  }
}
