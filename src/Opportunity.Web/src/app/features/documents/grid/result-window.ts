import type { SearchHit, SearchResultPage } from '../../../core/api/generated/models';

/** One server page held by the grid, with the cursors to its neighbours. */
export interface LoadedPage {
  /** 1-based page number when the server knows it (from the top, or from the end with an exact total). */
  readonly number: number | null;
  readonly items: readonly SearchHit[];
  readonly next: string | null;
  readonly previous: string | null;
}

export function toLoadedPage(page: SearchResultPage): LoadedPage {
  return {
    number: page.page.number === null ? null : Number(page.page.number),
    items: page.items,
    next: page.nextCursor ?? null,
    previous: page.previousCursor ?? null,
  };
}

/**
 * The contiguous run of cursor pages the grid has loaded (E16-T02): scrolling appends the next page or prepends
 * the previous one, and scrolling back reads these cached pages instead of asking the server again. A jump
 * (First when page 1 is not loaded, Last) starts a new run. Immutable: every change returns a new window.
 *
 * Rows are addressed by their index in the window. Pages can hold fewer rows than the page size (hits the user
 * may not see are dropped server-side, Q-12), so a row's position in the whole result comes from its page's
 * number, not from its index.
 */
export class ResultWindow {
  static readonly EMPTY = new ResultWindow([], 0);

  /** All loaded rows, in order. */
  readonly rows: readonly SearchHit[];
  /** Index of the first row of each page. */
  private readonly starts: readonly number[];

  private constructor(
    readonly pages: readonly LoadedPage[],
    readonly pageSize: number,
  ) {
    const starts: number[] = [];
    const rows: SearchHit[] = [];
    for (const page of pages) {
      starts.push(rows.length);
      for (const item of page.items) rows.push(item);
    }
    this.rows = rows;
    this.starts = starts;
  }

  static of(page: LoadedPage, pageSize: number): ResultWindow {
    return new ResultWindow([page], pageSize);
  }

  get first(): LoadedPage | undefined {
    return this.pages[0];
  }

  get last(): LoadedPage | undefined {
    return this.pages[this.pages.length - 1];
  }

  get hasNext(): boolean {
    return !!this.last?.next;
  }

  get hasPrevious(): boolean {
    return !!this.first?.previous;
  }

  /** The window with `page` added after the last page. */
  append(page: LoadedPage): ResultWindow {
    return new ResultWindow([...this.pages, page], this.pageSize);
  }

  /** The window with `page` added before the first page; existing rows move down by `page.items.length`. */
  prepend(page: LoadedPage): ResultWindow {
    return new ResultWindow([page, ...this.pages], this.pageSize);
  }

  /** Index in `pages` of the page holding row `row` (the nearest page for an index out of range). */
  pageIndexOf(row: number): number {
    let lo = 0;
    let hi = this.starts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (this.starts[mid] <= row) lo = mid;
      else hi = mid - 1;
    }
    return Math.max(0, lo);
  }

  /** Index of the first row of page `pageIndex`. */
  startOf(pageIndex: number): number {
    return this.starts[pageIndex] ?? this.rows.length;
  }

  /**
   * 1-based position of a row in the whole result, when known: its page number gives the page's first position,
   * the offset within the page the rest. Null on pages without a number (reached from Last of an inexact total).
   */
  position(row: number): number | null {
    const p = this.pageIndexOf(row);
    const page = this.pages[p];
    if (!page || page.number === null) return null;
    return (page.number - 1) * this.pageSize + (row - this.starts[p]) + 1;
  }

  indexOf(documentId: string): number {
    return this.rows.findIndex((r) => r.documentId === documentId);
  }
}
