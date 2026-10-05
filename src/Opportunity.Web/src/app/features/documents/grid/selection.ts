import type { SearchExpand, TotalCount } from '../../../core/api/generated/models';

/**
 * Most documents a selection may name one by one (E16-T06). A larger set is chosen as "all results", which is
 * sent as the search's query and the generation it was served at, never as a list of ids.
 */
export const MAX_SELECTED_IDS = 1_000;

/** "All results": the whole search, including rows that were never loaded. */
export interface AllResultsSelection {
  readonly kind: 'all';
  /** The effective query of the list (keyword query ANDed with the filters). */
  readonly query: string;
  /** Search generation the list was served at (null when the server did not say). */
  readonly generation: string | null;
  readonly total: TotalCount;
  /** The list's count label when it was selected ("58,330", "≈ 58,330", "≥ 10,000 (approx.)"). */
  readonly countText: string;
  /** "Include: Family / Duplicates / Email thread" of the list: frozen with the same expansion (E09-T03). */
  readonly expand?: SearchExpand | null;
}

/** Documents checked one by one (rows, pages, Shift ranges). */
export interface DocumentSelection {
  readonly kind: 'documents';
  readonly documentIds: readonly string[];
}

/** What Mass Actions act on: frozen into a snapshot before anything changes (ADR-002). */
export type SelectionTarget = AllResultsSelection | DocumentSelection;

/** Selected count for the header and the live region: "3", or "all ≈ 58,330 results". */
export function selectionLabel(target: SelectionTarget | null, locale: string): string {
  if (!target) return '0';
  if (target.kind === 'all') return `all ${target.countText} results`;
  return new Intl.NumberFormat(locale).format(target.documentIds.length);
}

/** Spoken after the selection changes. */
export function selectionAnnouncement(target: SelectionTarget | null, locale: string): string {
  if (!target) return 'Selection cleared.';
  if (target.kind === 'all') return `All ${target.countText} results selected.`;
  const n = target.documentIds.length;
  return n === 1
    ? '1 document selected.'
    : `${new Intl.NumberFormat(locale).format(n)} documents selected.`;
}

/** Why a selection cannot go to Mass Actions as it is, or null when it can. */
export function selectionProblem(target: SelectionTarget | null): string | null {
  if (!target || (target.kind === 'documents' && target.documentIds.length === 0)) {
    return 'Select documents first: check rows, a page, or all results.';
  }
  if (target.kind === 'documents' && target.documentIds.length > MAX_SELECTED_IDS) {
    return `More than ${MAX_SELECTED_IDS.toLocaleString('en')} documents are checked one by one. Select all results instead, after narrowing the search with a filter if needed.`;
  }
  return null;
}

/** Row indexes from `a` to `b`, both included, in either order (Shift+click ranges). */
export function rangeBetween(a: number, b: number): number[] {
  const [from, to] = a <= b ? [a, b] : [b, a];
  return Array.from({ length: to - from + 1 }, (_, i) => from + i);
}
