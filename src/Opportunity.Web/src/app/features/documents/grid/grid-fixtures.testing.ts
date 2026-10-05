import type { SearchHit, SearchResultPage } from '../../../core/api/generated/models';

/** A result row with control number `ACM` + 7 digits for 1-based `n`. */
export function hit(n: number, overrides: Partial<SearchHit> = {}): SearchHit {
  return {
    documentId: `doc-${n}`,
    controlNumber: `ACM${String(n).padStart(7, '0')}`,
    documentDate: '2024-03-01T14:30:00Z',
    familyId: `doc-${n}`,
    familySequence: 0,
    fileExtension: 'msg',
    fileName: `Message ${n}.msg`,
    fileSize: 2048 * n,
    fileType: 'Email',
    isFamilyParent: false,
    mimeType: 'application/vnd.ms-outlook',
    pageCount: 2,
    parentDocumentId: null,
    snippets: [],
    ...overrides,
  };
}

export interface FakeResultOptions {
  total: number;
  pageSize: number;
  /** Totals above this are reported as a lower bound (Q-32). */
  cap?: number;
  current?: boolean | null;
  searchId?: string;
  /** Wave-10 freshness additions (`state`, `pendingChanges`, `lagSeconds`, …), merged into `freshness`. */
  freshness?: Record<string, unknown>;
}

/**
 * Pages of a search over `total` rows the way the search service serves them (numbers from the top, Last from
 * the end with page boundaries kept when the total is exact). Cursors encode the page number: `p<n>`.
 */
export function fakePage(options: FakeResultOptions, number: number): SearchResultPage {
  const { total, pageSize, cap = 10_000, current = true, searchId = 'search-1' } = options;
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const exact = total <= cap;
  const first = (number - 1) * pageSize + 1;
  const items = Array.from({ length: Math.max(0, Math.min(pageSize, total - first + 1)) }, (_, i) =>
    hit(first + i),
  );
  return {
    searchId,
    normalized: '',
    items,
    page: {
      number,
      size: pageSize,
      pageCount: exact ? pageCount : null,
      isFirst: number === 1,
      isLast: number >= pageCount,
    },
    total: { value: exact ? total : cap, relation: exact ? 'eq' : 'gte' },
    freshness: {
      asOf: '2026-10-04T10:42:00Z',
      current,
      servedGeneration: null,
      state: current ? 'current' : 'updating',
      indexedThroughGeneration: 0,
      pendingChanges: current ? 0 : 1,
      lagSeconds: 0,
      ...options.freshness,
    },
    nextCursor: number < pageCount ? `p${number + 1}` : null,
    previousCursor: number > 1 ? `p${number - 1}` : null,
    resultsRefreshed: false,
  };
}
