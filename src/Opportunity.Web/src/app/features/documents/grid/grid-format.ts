import type { SearchFreshness, TotalCount } from '../../../core/api/generated/models';
import { toServedFreshness } from '../../../core/search/search-freshness';
import { formatNumber, Formatter, formatterFor } from '../../../core/i18n';

/**
 * Count label of the results header (Q-10, Q-32, familiarity guide §6): exact counts with thousands separators;
 * "≈" while the served index is not known to be current; a capped count as "≥ 10,000 (approx.)"; approximate
 * counts of a million or more abbreviated ("~1.2M"). Never a bare capped number.
 */
export function countLabel(total: TotalCount, freshness: SearchFreshness, locale: string): string {
  const value = Number(total.value);
  const n = formatNumber(value, locale);
  if (total.relation === 'gte') return `≥ ${n} (approx.)`;
  if (freshness.current === true) return n;
  if (value >= 1_000_000) {
    return `~${formatterFor(locale).compact(value)}`;
  }
  return `≈ ${n}`;
}

/** Whether counts are shown as approximate (the "≈" of `countLabel`). */
export function isApproximate(total: TotalCount, freshness: SearchFreshness): boolean {
  return total.relation === 'gte' || freshness.current !== true;
}

/**
 * The stamp of a result set (Q-10, E16-T07): when the index it was served from was current, in plain words —
 * "Results current as of 10:42 AM UTC", or "Results as of 10:42 AM UTC" while changes were still pending (the
 * footnote then says how many). Reviewers never see generation numbers; admins find them in the freshness detail.
 */
export function freshnessLabel(freshness: SearchFreshness, format: Formatter): string {
  const time = format.time(freshness.asOf as string);
  // `current` describes this result set; `state` the workspace index at serve time (#70), which can differ.
  const current =
    typeof freshness.current === 'boolean'
      ? freshness.current
      : toServedFreshness(freshness).state === 'current';
  return current ? `Results current as of ${time}` : `Results as of ${time}`;
}
