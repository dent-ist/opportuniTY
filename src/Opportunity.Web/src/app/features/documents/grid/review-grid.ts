import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ApiError, UserFacingError, toApiError } from '../../../core/api/problem-details';
import type {
  FieldResource,
  SearchFreshness,
  SearchHit,
  SearchResultPage,
  SearchSortKey,
  TotalCount,
} from '../../../core/api/generated/models';
import { CommandRegistry, CommandScopeDirective } from '../../../core/commands';
import { PreferenceStorage } from '../../../core/preferences/preference-storage';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Announcer, Button, EmptyState, ErrorState, Icon, LoadingState } from '../../../ui';
import { GridColumn, defaultColumns, familyMarker } from './grid-columns';
import { CellFormatter, countLabel, freshnessLabel } from './grid-format';
import { PageRequest, ReviewSearchApi } from './review-search';
import { LoadedPage, ResultWindow, toLoadedPage } from './result-window';

/** What the document list shows. */
export interface GridSearch {
  /** Query text; empty lists every document. A new object runs the search again. */
  readonly query: string;
}

/** A row opened with Enter, double-click (Review mode, E16-T03). */
export interface GridOpenEvent {
  readonly hit: SearchHit;
  /** Index of the row among the loaded rows. */
  readonly index: number;
}

/** Rows fetched per cursor page (familiarity review: 50 / 100 / 250 / 500, pending P-2). */
export const PAGE_SIZES = [50, 100, 250, 500] as const;
export const DEFAULT_PAGE_SIZE = 100;
const PAGE_SIZE_KEY = 'grid.pageSize';

/** Rows rendered above and below the viewport. */
const BUFFER_ROWS = 10;
/** Fallback before layout (and in environments without layout). */
const DEFAULT_ROW_PX = 32;
const DEFAULT_VISIBLE_ROWS = 20;

/** Column index of the fixed columns; View columns follow. */
const COL_SELECT = 0;
const COL_CONTROL = 1;
const FIXED_COLUMNS = 3;

type Status = 'loading' | 'ready' | 'empty' | 'not-indexed' | 'error';

interface ResultInfo {
  readonly searchId: string;
  readonly total: TotalCount;
  readonly freshness: SearchFreshness;
  readonly pageCount: number | null;
}

/**
 * The document list of List mode (E16-T02, familiarity guide §3.1): a virtualized ARIA grid over cursor pages
 * of a search (`POST …/searches`, `GET …/searches/{id}/pages`).
 *
 * - Only the visible rows plus a buffer are in the DOM. Scrolling near either end fetches the neighbouring page
 *   by cursor; pages already loaded are kept, so scrolling back never asks the server again.
 * - Paging (Q-49): "Page n of N" with First / Previous / Next / Last. Last reverses the sort server-side.
 * - Counts (Q-10, Q-32): exact, "≈" or "≥ 10,000 (approx.)" with Count exactly; "Current as of hh:mm".
 * - Q-33: when the server reopened its point-in-time view, or the search expired and is run again, the list
 *   keeps the focused document where it can and shows "Results refreshed".
 * - Keyboard: arrows, PageUp/PageDown, Home/End (Ctrl too) move the focused row, Left/Right the cell; Enter
 *   on a header sorts. Commands of the `grid` scope (open, select row/page, clear) come from the registry.
 * - Selection is kept by document id and exposed for Mass Actions (E16-T06).
 */
@Component({
  selector: 'opp-review-grid',
  imports: [
    Button,
    CommandScopeDirective,
    EmptyState,
    ErrorState,
    Icon,
    LoadingState,
    NgTemplateOutlet,
  ],
  providers: [ReviewSearchApi],
  templateUrl: './review-grid.html',
  styleUrl: './review-grid.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewGrid {
  readonly search = input.required<GridSearch>();
  readonly open = output<GridOpenEvent>();
  readonly selectionChange = output<ReadonlySet<string>>();

  private readonly api = inject(ReviewSearchApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly storage = inject(PreferenceStorage);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');

  protected readonly canSearch = this.context.can(PERMISSIONS.searchExecute);
  protected readonly gridId = `grid-${this.context.workspaceId}`;

  // Columns
  private readonly fields = signal<FieldResource[] | null>(null);
  protected readonly columns = computed(() => defaultColumns(this.fields()));
  protected readonly colCount = computed(() => FIXED_COLUMNS + this.columns().view.length);
  protected readonly gridTemplate = computed(() =>
    [
      '2.25rem',
      this.columns().controlNumber.width,
      '2.25rem',
      ...this.columns().view.map((c) => c.width),
    ].join(' '),
  );
  protected readonly gridMinWidth = computed(() => {
    const rem = [2.25, 10, 2.25, ...this.columns().view.map((c) => minRem(c.width))];
    return `${rem.reduce((a, b) => a + b, 0)}rem`;
  });
  protected readonly format = computed(
    () => new CellFormatter(this.prefs.locale(), this.context.workspace.displayTimeZone || 'UTC'),
  );

  // Search state
  protected readonly status = signal<Status>('loading');
  protected readonly error = signal<ApiError | UserFacingError | null>(null);
  protected readonly window = signal(ResultWindow.EMPTY);
  protected readonly rows = computed(() => this.window().rows);
  protected readonly result = signal<ResultInfo | null>(null);
  protected readonly sort = signal<SearchSortKey | null>(null);
  protected readonly busy = signal(false);
  protected readonly loadingMore = signal<'next' | 'previous' | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly pageSize = signal<number>(
    storedPageSize(this.storage.read<number>(PAGE_SIZE_KEY)),
  );
  private countExact = false;
  /** Bumped by every new search or jump; responses for an older value are dropped. */
  private seq = 0;

  // Focus and selection
  protected readonly focusRow = signal(0);
  protected readonly focusCol = signal(COL_CONTROL);
  private readonly _selected = signal<ReadonlySet<string>>(new Set());
  /** Document ids of the selected rows (Mass Actions, E16-T06). */
  readonly selection = this._selected.asReadonly();

  // Virtualization
  protected readonly rowHeight = signal(DEFAULT_ROW_PX);
  protected readonly scrollRow = signal(0);
  protected readonly visibleCount = signal(DEFAULT_VISIBLE_ROWS);
  /** The columns are wider than the grid, so Control Number and the checkbox are pinned. */
  protected readonly wide = signal(false);
  protected readonly range = computed(() => {
    const total = this.rows().length;
    const start = Math.max(0, Math.min(this.scrollRow(), total) - BUFFER_ROWS);
    const end = Math.min(total, this.scrollRow() + this.visibleCount() + BUFFER_ROWS);
    return { start, end };
  });
  protected readonly visibleRows = computed(() => {
    const { start, end } = this.range();
    return this.rows().slice(start, end);
  });

  // Labels
  protected readonly countText = computed(() => {
    const r = this.result();
    return r ? countLabel(r.total, r.freshness, this.prefs.locale()) : '';
  });
  protected readonly freshnessText = computed(() => {
    const r = this.result();
    return r
      ? freshnessLabel(
          r.freshness,
          this.prefs.locale(),
          this.context.workspace.displayTimeZone || 'UTC',
        )
      : '';
  });
  protected readonly approximateTotal = computed(() => this.result()?.total.relation === 'gte');
  protected readonly ariaRowCount = computed(() => {
    const r = this.result();
    return r && r.total.relation === 'eq' ? Number(r.total.value) + 1 : -1;
  });
  /** The page whose rows the user is looking at: the focused row's when it is on screen, else the top row's. */
  protected readonly currentPage = computed(() => {
    const w = this.window();
    const focus = this.focusRow();
    const top = this.scrollRow();
    const row = focus >= top && focus < top + this.visibleCount() ? focus : top;
    return w.pageIndexOf(row);
  });
  protected readonly pageText = computed(() => {
    const w = this.window();
    const page = w.pages[this.currentPage()];
    const r = this.result();
    if (!page || !r) return '';
    const n = new Intl.NumberFormat(this.prefs.locale());
    if (page.number === null) return page.next ? 'Page —' : 'Last page';
    if (r.pageCount !== null) return `Page ${n.format(page.number)} of ${n.format(r.pageCount)}`;
    const atLeast = Math.ceil(Number(r.total.value) / w.pageSize);
    return `Page ${n.format(page.number)} of ≥ ${n.format(atLeast)}`;
  });
  protected readonly canGoBack = computed(
    () => this.currentPage() > 0 || this.window().hasPrevious,
  );
  protected readonly canGoForward = computed(
    () => this.currentPage() < this.window().pages.length - 1 || this.window().hasNext,
  );
  protected readonly rowsText = computed(() => {
    const w = this.window();
    if (w.rows.length === 0) return '';
    const first = Math.min(this.scrollRow(), w.rows.length - 1);
    const last = Math.min(w.rows.length - 1, this.scrollRow() + this.visibleCount() - 1);
    const a = w.position(first);
    const b = w.position(last);
    const n = new Intl.NumberFormat(this.prefs.locale());
    return a !== null && b !== null
      ? `Rows ${n.format(a)}–${n.format(b)} of ${this.countText()}`
      : `${n.format(last - first + 1)} rows near the end of ${this.countText()}`;
  });
  protected readonly selectedOnPage = computed(() => {
    const ids = this.pageRows(this.currentPage()).map((r) => r.documentId);
    const selected = this.selection();
    const count = ids.filter((id) => selected.has(id)).length;
    return { all: ids.length > 0 && count === ids.length, some: count > 0 && count < ids.length };
  });
  protected readonly activeDescendant = computed(() => {
    const row = this.focusRow();
    const { start, end } = this.range();
    if (row === -1) return this.headerCellId(this.focusCol());
    return row >= start && row < end && this.rows().length > 0
      ? this.cellId(row, this.focusCol())
      : null;
  });

  constructor() {
    const registry = inject(CommandRegistry);
    const ready = { enabled: () => this.status() === 'ready' };
    registry.handle('grid.openDocument', () => this.openFocused(), {
      enabled: () => this.status() === 'ready' && this.focusRow() >= 0,
    });
    registry.handle('selection.toggleRow', () => this.toggleRow(this.focusRow()), {
      enabled: () => this.status() === 'ready' && this.focusRow() >= 0,
    });
    registry.handle('selection.allOnPage', () => this.selectPage(true), ready);
    registry.handle('selection.clear', () => this.setSelection(new Set()), ready);

    if (this.canSearch) {
      this.api.fields().then(
        (fields) => this.fields.set(fields),
        () => undefined, // the structural defaults stay
      );
    }

    effect(() => {
      this.search();
      untracked(() => this.run());
    });

    effect(() => {
      // Density changes the row height; the columns whether the grid scrolls sideways.
      this.prefs.density();
      this.columns();
      this.status();
      afterNextRender(() => this.measure(), { injector: this.injector });
    });

    const resize =
      typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => this.measure());
    afterNextRender(() => {
      this.measure();
      const el = this.viewport()?.nativeElement;
      if (el) resize?.observe(el);
    });
    inject(DestroyRef).onDestroy(() => resize?.disconnect());
  }

  // ── Searching ────────────────────────────────────────────────────────────────────────────────────────────

  /** Runs the search (again), keeping `anchor` focused when it is in the first page. */
  private async run(options: { anchor?: string | null; notice?: string } = {}): Promise<void> {
    if (!this.canSearch) {
      this.status.set('error');
      this.error.set({
        title: 'No access to search',
        detail: 'You do not have permission to search or list documents in this workspace.',
        retryable: false,
      });
      return;
    }
    const seq = ++this.seq;
    if (this.status() !== 'ready') this.status.set('loading');
    this.busy.set(true);
    this.loadError.set(null);
    const sort = this.sort();
    let page: SearchResultPage;
    try {
      page = await this.api.run({
        query: this.search().query,
        sort: sort ? [sort] : null,
        countExact: this.countExact || null,
        pageSize: this.pageSize(),
        highlight: false,
      });
    } catch (e) {
      if (seq !== this.seq) return;
      this.busy.set(false);
      this.fail(toApiError(e));
      return;
    }
    if (seq !== this.seq) return;
    this.busy.set(false);
    this.error.set(null);
    if (!page.searchId) {
      this.window.set(ResultWindow.EMPTY);
      this.result.set(null);
      this.status.set('not-indexed');
      return;
    }
    const window = ResultWindow.of(toLoadedPage(page), this.pageSize());
    this.window.set(window);
    this.result.set(resultInfo(page.searchId, page));
    this.status.set(window.rows.length > 0 ? 'ready' : 'empty');
    const anchor = options.anchor ? window.indexOf(options.anchor) : -1;
    // A sort started from a header keeps focus there.
    if (this.focusRow() !== -1) this.focusRow.set(Math.max(0, anchor));
    this.scrollTo(Math.max(0, anchor), anchor >= 0 ? 'nearest' : 'start');
    const notice = options.notice ?? (page.resultsRefreshed ? REFRESHED : null);
    this.notice.set(notice);
    this.announcer.announce(notice ?? `${this.countText()} documents`);
  }

  private fail(error: ApiError): void {
    this.window.set(ResultWindow.EMPTY);
    this.result.set(null);
    this.status.set('error');
    if (error.status === 403) {
      this.error.set({
        title: 'No access to search',
        detail: 'You do not have permission to search or list documents in this workspace.',
        retryable: false,
      });
    } else if (error.code === 'invalid-query') {
      const first = (error.problem['queryErrors'] as { message?: string }[] | undefined)?.[0];
      this.error.set({
        title: 'The query cannot be run',
        detail: first?.message ?? 'Correct the query and search again.',
        reference: error.traceId,
        retryable: false,
      });
    } else {
      this.error.set(error);
    }
  }

  protected retry(): void {
    void this.run();
  }

  protected countExactly(): void {
    this.countExact = true;
    void this.run({ anchor: this.focusedId() });
  }

  protected setPageSize(event: Event): void {
    const size = storedPageSize(Number((event.target as HTMLSelectElement).value));
    this.pageSize.set(size);
    this.storage.write(PAGE_SIZE_KEY, size);
    void this.run({ anchor: this.focusedId() });
  }

  /** Header click or Enter on a header: ascending → descending → back to relevance. */
  protected toggleSort(column: GridColumn): void {
    if (!column.sortField) return;
    const current = this.sort();
    const next: SearchSortKey | null =
      current?.field !== column.sortField
        ? { field: column.sortField, direction: 'asc' }
        : current.direction === 'asc'
          ? { field: column.sortField, direction: 'desc' }
          : null;
    this.sort.set(next);
    void this.run({ anchor: this.focusedId() });
  }

  protected ariaSort(column: GridColumn): string | null {
    const sort = this.sort();
    if (!column.sortField || sort?.field !== column.sortField) return null;
    return sort.direction === 'desc' ? 'descending' : 'ascending';
  }

  /** Fetches the page after the last (or before the first) loaded page by cursor. */
  private async loadMore(direction: 'next' | 'previous'): Promise<boolean> {
    const window = this.window();
    const result = this.result();
    const cursor = direction === 'next' ? window.last?.next : window.first?.previous;
    if (!cursor || !result || this.loadingMore() || this.busy()) return false;
    const seq = this.seq;
    this.loadingMore.set(direction);
    this.loadError.set(null);
    let page: SearchResultPage;
    try {
      page = await this.api.page(result.searchId, { cursor });
    } catch (e) {
      if (seq !== this.seq) return false;
      this.loadingMore.set(null);
      this.pageFailed(toApiError(e));
      return false;
    }
    this.loadingMore.set(null);
    if (seq !== this.seq || this.window() !== window) return false;
    const loaded = toLoadedPage(page);
    this.result.set(resultInfo(result.searchId, page));
    if (page.resultsRefreshed) this.refreshed();
    if (direction === 'next') {
      this.window.set(window.append(loaded));
    } else {
      this.window.set(window.prepend(loaded));
      this.shiftRows(loaded.items.length);
    }
    return true;
  }

  /** First when page 1 is not loaded, Last: a new run of pages. */
  private async jump(request: PageRequest): Promise<void> {
    const result = this.result();
    if (!result || this.busy()) return;
    const seq = ++this.seq;
    this.busy.set(true);
    this.loadError.set(null);
    let page: SearchResultPage;
    try {
      page = await this.api.page(result.searchId, request);
    } catch (e) {
      if (seq !== this.seq) return;
      this.busy.set(false);
      this.pageFailed(toApiError(e));
      return;
    }
    if (seq !== this.seq) return;
    this.busy.set(false);
    const loaded = toLoadedPage(page);
    const window = this.window();
    this.result.set(resultInfo(result.searchId, page));
    if (page.resultsRefreshed) this.refreshed();
    if (adjacent(window.last, loaded)) {
      // Last is the page after the loaded ones: keep them (the cache) and add it.
      this.window.set(window.append(loaded));
      this.focusPage(window.pages.length);
    } else if (adjacent(loaded, window.first)) {
      this.window.set(window.prepend(loaded));
      this.shiftRows(loaded.items.length);
      this.focusPage(0);
    } else {
      this.window.set(ResultWindow.of(loaded, window.pageSize));
      this.focusPage(0);
    }
  }

  private pageFailed(error: ApiError): void {
    if (error.status === 404) {
      // The search expired (idle timeout) or its handle is gone: run it again from the top (Q-33).
      void this.run({ anchor: this.focusedId(), notice: EXPIRED });
      return;
    }
    this.loadError.set(
      error.status === 0 ? 'Cannot reach the server.' : 'More documents could not be loaded.',
    );
  }

  protected retryLoad(): void {
    this.loadError.set(null);
    this.maybeLoadMore();
  }

  private refreshed(): void {
    this.notice.set(REFRESHED);
    this.announcer.announce(REFRESHED);
  }

  // ── Paging (Q-49) ────────────────────────────────────────────────────────────────────────────────────────

  protected async goFirst(): Promise<void> {
    if (this.window().first?.number === 1) this.focusPage(0);
    else await this.jump({ page: 1 });
  }

  protected async goPrevious(): Promise<void> {
    const p = this.currentPage();
    if (p > 0) this.focusPage(p - 1);
    else if (await this.loadMore('previous')) this.focusPage(0);
  }

  protected async goNext(): Promise<void> {
    const p = this.currentPage();
    if (p < this.window().pages.length - 1) this.focusPage(p + 1);
    else if (await this.loadMore('next')) this.focusPage(p + 1);
  }

  protected async goLast(): Promise<void> {
    const w = this.window();
    if (!w.hasNext) this.focusPage(w.pages.length - 1);
    else await this.jump({ last: true });
  }

  private focusPage(index: number): void {
    const row = this.window().startOf(index);
    this.focusRow.set(row);
    this.scrollTo(row, 'start');
  }

  private pageRows(index: number): readonly SearchHit[] {
    return this.window().pages[index]?.items ?? [];
  }

  // ── Scrolling ────────────────────────────────────────────────────────────────────────────────────────────

  protected onScroll(): void {
    const el = this.viewport()?.nativeElement;
    if (!el) return;
    this.scrollRow.set(Math.floor(el.scrollTop / this.rowHeight()));
    this.maybeLoadMore();
  }

  /** Prefetches the neighbouring page when the viewport is within a screenful of either end. */
  private maybeLoadMore(): void {
    if (this.status() !== 'ready' || this.loadError()) return;
    const top = this.scrollRow();
    const visible = this.visibleCount();
    if (this.rows().length - (top + visible) < visible && this.window().hasNext) {
      void this.loadMore('next');
    } else if (top < visible && this.window().hasPrevious) {
      void this.loadMore('previous');
    }
  }

  /** Brings `row` on screen: at the top (`start`) or only as far as needed (`nearest`). */
  private scrollTo(row: number, mode: 'start' | 'nearest'): void {
    const h = this.rowHeight();
    const visible = this.visibleCount();
    const top = this.scrollRow();
    let target = top;
    if (mode === 'start' || row < top) target = row;
    else if (row > top + visible - 1) target = row - visible + 1;
    target = Math.max(0, Math.min(target, Math.max(0, this.rows().length - visible)));
    this.scrollRow.set(target);
    // The body may grow in this change detection; scroll once it has.
    afterNextRender(
      {
        write: () => {
          const el = this.viewport()?.nativeElement;
          if (el && Math.floor(el.scrollTop / h) !== target) el.scrollTop = target * h;
          this.maybeLoadMore();
        },
      },
      { injector: this.injector },
    );
  }

  /** `count` rows were inserted above: keep the same documents on screen and focused. */
  private shiftRows(count: number): void {
    this.focusRow.update((r) => r + count);
    if (this.anchor >= 0) this.anchor += count;
    this.scrollRow.update((r) => r + count);
    const h = this.rowHeight();
    afterNextRender(
      {
        write: () => {
          const el = this.viewport()?.nativeElement;
          if (el) el.scrollTop += count * h;
        },
      },
      { injector: this.injector },
    );
  }

  private measure(): void {
    const el = this.viewport()?.nativeElement;
    if (!el) return;
    const style = getComputedStyle(el);
    const value = style.getPropertyValue('--opp-row-height').trim();
    const rootPx = parseFloat(getComputedStyle(el.ownerDocument.documentElement).fontSize) || 16;
    const px = value.endsWith('rem')
      ? parseFloat(value) * rootPx
      : value.endsWith('px')
        ? parseFloat(value)
        : NaN;
    const h = Number.isFinite(px) && px > 0 ? px : DEFAULT_ROW_PX;
    this.rowHeight.set(h);
    const head = el.querySelector<HTMLElement>('.grid__head')?.offsetHeight ?? h;
    const usable = el.clientHeight - head;
    this.visibleCount.set(usable > 0 ? Math.max(1, Math.ceil(usable / h)) : DEFAULT_VISIBLE_ROWS);
    this.scrollRow.set(Math.floor(el.scrollTop / h));
    this.wide.set(el.scrollWidth > el.clientWidth + 1);
  }

  // ── Keyboard and pointer ─────────────────────────────────────────────────────────────────────────────────

  protected onKeydown(event: KeyboardEvent): void {
    if (event.altKey || event.metaKey || this.status() !== 'ready') return;
    const last = this.rows().length - 1;
    const page = Math.max(1, this.visibleCount() - 1);
    const row = this.focusRow();
    let target: number | null = null;
    switch (event.key) {
      case 'ArrowDown':
        target = Math.min(last, row + 1);
        break;
      case 'ArrowUp':
        target = row - 1 < 0 ? -1 : row - 1;
        break;
      case 'PageDown':
        target = Math.min(last, Math.max(0, row) + page);
        break;
      case 'PageUp':
        target = Math.max(0, row - page);
        break;
      case 'Home':
        target = 0;
        break;
      case 'End':
        target = last;
        break;
      case 'ArrowLeft':
        this.focusCol.update((c) => Math.max(0, c - 1));
        event.preventDefault();
        return;
      case 'ArrowRight':
        this.focusCol.update((c) => Math.min(this.colCount() - 1, c + 1));
        event.preventDefault();
        return;
      case 'Enter':
      case ' ':
        if (row === -1 && !event.ctrlKey && !event.shiftKey) {
          event.preventDefault();
          this.activateHeader(this.focusCol());
        }
        return;
      default:
        return;
    }
    event.preventDefault();
    if (event.ctrlKey && event.key !== 'Home' && event.key !== 'End') return;
    if (
      event.shiftKey &&
      target >= 0 &&
      row >= 0 &&
      (event.key === 'ArrowDown' || event.key === 'ArrowUp')
    ) {
      this.extendSelection(row, target);
    }
    this.focusRow.set(target);
    if (target >= 0) this.scrollTo(target, 'nearest');
  }

  private activateHeader(col: number): void {
    if (col === COL_SELECT) this.selectPage(!this.selectedOnPage().all);
    else if (col === COL_CONTROL) this.toggleSort(this.columns().controlNumber);
    else if (col >= FIXED_COLUMNS) this.toggleSort(this.columns().view[col - FIXED_COLUMNS]);
  }

  protected onRowClick(index: number, col: number): void {
    this.focusRow.set(index);
    this.focusCol.set(col);
    this.viewport()?.nativeElement.focus({ preventScroll: true });
  }

  protected onHeaderClick(col: number): void {
    this.focusRow.set(-1);
    this.focusCol.set(col);
    this.viewport()?.nativeElement.focus({ preventScroll: true });
    this.activateHeader(col);
  }

  protected openRow(index: number): void {
    const hit = this.rows()[index];
    if (hit) this.open.emit({ hit, index });
  }

  private openFocused(): void {
    this.openRow(this.focusRow());
  }

  private focusedId(): string | null {
    return this.rows()[this.focusRow()]?.documentId ?? null;
  }

  // ── Selection ────────────────────────────────────────────────────────────────────────────────────────────

  private anchor = -1;

  protected isSelected(hit: SearchHit): boolean {
    return this.selection().has(hit.documentId);
  }

  protected toggleRow(index: number): void {
    const hit = this.rows()[index];
    if (!hit) return;
    const next = new Set(this.selection());
    if (next.has(hit.documentId)) next.delete(hit.documentId);
    else next.add(hit.documentId);
    this.anchor = index;
    this.setSelection(next);
  }

  private extendSelection(from: number, to: number): void {
    const rows = this.rows();
    const next = new Set(this.selection());
    for (const i of [from, to]) if (rows[i]) next.add(rows[i].documentId);
    this.anchor = to;
    this.setSelection(next);
  }

  protected selectPage(select: boolean): void {
    const next = new Set(this.selection());
    for (const hit of this.pageRows(this.currentPage())) {
      if (select) next.add(hit.documentId);
      else next.delete(hit.documentId);
    }
    this.setSelection(next);
  }

  private setSelection(next: ReadonlySet<string>): void {
    this._selected.set(next);
    this.selectionChange.emit(next);
  }

  /** Clears the selection (Mass Actions, after a run). */
  clearSelection(): void {
    this.setSelection(new Set());
  }

  // ── Rendering helpers ────────────────────────────────────────────────────────────────────────────────────

  protected cellId(row: number, col: number): string {
    return `${this.gridId}-r${row}-c${col}`;
  }

  protected headerCellId(col: number): string {
    return `${this.gridId}-h-c${col}`;
  }

  protected rowIndex(index: number): number {
    const position = this.window().position(index);
    // Header is row 1. Without a known position (end of an inexact result) rows are numbered as loaded.
    return (position ?? index + 1) + 1;
  }

  /** Formatted View cells of a row, built once per row while the columns and formats stay the same. */
  private readonly cellCache = computed(() => {
    this.columns();
    this.format();
    return new WeakMap<SearchHit, string[]>();
  });

  protected rowCells(hit: SearchHit): string[] {
    const cache = this.cellCache();
    let cells = cache.get(hit);
    if (!cells) {
      cells = this.columns().view.map((column) => this.cell(hit, column));
      cache.set(hit, cells);
    }
    return cells;
  }

  private cell(hit: SearchHit, column: GridColumn): string {
    const value = column.value(hit);
    if (value === null || value === '') return '';
    const f = this.format();
    switch (column.format) {
      case 'date':
        return f.formatDate(String(value));
      case 'size':
        return f.formatSize(Number(value));
      case 'number':
        return f.formatNumber(Number(value));
      default:
        return String(value);
    }
  }

  protected readonly familyMarker = familyMarker;
  protected readonly COL_SELECT = COL_SELECT;
  protected readonly COL_CONTROL = COL_CONTROL;
  protected readonly FIXED_COLUMNS = FIXED_COLUMNS;
  protected readonly PAGE_SIZES = PAGE_SIZES;
}

const REFRESHED = 'Results refreshed: the list now includes recent changes.';
const EXPIRED = 'Results refreshed: the search had expired and was run again.';

/** `b` directly follows `a` (both numbered). */
function adjacent(a: LoadedPage | undefined, b: LoadedPage | undefined): boolean {
  return !!a && !!b && a.number !== null && b.number === a.number + 1;
}

function resultInfo(searchId: string, page: SearchResultPage): ResultInfo {
  return {
    searchId,
    total: page.total,
    freshness: page.freshness,
    pageCount: page.page.pageCount === null ? null : Number(page.page.pageCount),
  };
}

function storedPageSize(value: number | undefined): number {
  return (PAGE_SIZES as readonly number[]).includes(value ?? NaN)
    ? (value as number)
    : DEFAULT_PAGE_SIZE;
}

/** The minimum of a CSS track size in rem (`10rem`, `minmax(12rem, 2fr)`). */
function minRem(track: string): number {
  return parseFloat(/([\d.]+)rem/.exec(track)?.[1] ?? '6');
}
