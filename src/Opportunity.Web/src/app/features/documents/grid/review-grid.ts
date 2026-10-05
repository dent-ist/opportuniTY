import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
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
  SearchExpand,
  SearchExpandedCounts,
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
import { Announcer, Button, EmptyState, ErrorState, Icon, LoadingState, MENU } from '../../../ui';
import { GridColumn, defaultColumns, familyMarker } from './grid-columns';
import { GridFilter, FilterChange } from './grid-filter';
import {
  CompiledQuery,
  FilterSet,
  FilterSpec,
  FilterValue,
  clauseAt,
  compileFilter,
  compileQuery,
  describeFilter,
  filterSpec,
  quotePhrase,
} from './grid-filters';
import { ActiveFilter, FilterSummary } from './filter-summary';
import { CellFormatter, countLabel, freshnessLabel } from './grid-format';
import {
  type ServedFreshness,
  footnoteText,
  toServedFreshness,
} from '../../../core/search/search-freshness';
import { FreshnessMonitor } from '../freshness/freshness-monitor';
import { PageRequest, ReviewSearchApi } from './review-search';
import { LoadedPage, ResultWindow, toLoadedPage } from './result-window';
import type { CursorSource } from '../review/review-cursor';
import { PendingCoding } from '../review/coding/pending-coding';
import {
  AllResultsSelection,
  SelectionTarget,
  rangeBetween,
  selectionAnnouncement,
  selectionLabel,
} from './selection';

/** What the document list shows. */
export interface GridSearch {
  /** Query text; empty lists every document. A new object runs the search again. */
  readonly query: string;
  /**
   * The saved search `query` came from (E16-T11): without filters the list runs it by id, so the server re-parses
   * the stored query and records the run and its hit count.
   */
  readonly savedSearchId?: string | null;
  /**
   * One term of a Search Terms Report (#180): without filters the list asks for that term's hits within the report's
   * frozen set (filtered for the caller); `query` is the term's expression, run live once filters narrow it.
   */
  readonly termReport?: { readonly reportId: string; readonly termId: string } | null;
  /** The page is still loading what to search (a saved search): show the loading state, run nothing yet. */
  readonly deferred?: boolean;
  /** "Include: Family / Duplicates / Email thread" (E09-T03); null: the hits only (or a saved search's stored choice). */
  readonly expand?: SearchExpand | null;
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
/** Whether the filter row is shown (a user preference, #191). */
export const FILTER_ROW_KEY = 'grid.filterRow';
/** Selected documents checked per request when a filter changes (control numbers ORed in one query). */
const VERIFY_CHUNK = 100;

/** Rows rendered above and below the viewport. */
const BUFFER_ROWS = 10;
/** Fallback before layout (and in environments without layout). */
const DEFAULT_ROW_PX = 32;
const DEFAULT_VISIBLE_ROWS = 20;

/** Column index of the fixed columns; View columns follow. */
const COL_SELECT = 0;
const COL_CONTROL = 1;
const FIXED_COLUMNS = 3;

/** `invalid`: the server rejected a filter; its message is shown under it. */
type Status = 'loading' | 'ready' | 'empty' | 'invalid' | 'not-indexed' | 'error';

interface QueryError {
  readonly message?: string;
  readonly span?: { readonly start: number | string };
}

interface RunOptions {
  anchor?: string | null;
  /** Where `anchor` was in the whole result: when it is not on the first page, its page is loaded instead. */
  anchorPosition?: number | null;
  notice?: string;
  /** A filter was added or changed: selected documents that left the results are deselected. */
  narrowed?: boolean;
}

interface ResultInfo {
  readonly searchId: string;
  /** The size of the list: the hits, or with Include the hits plus the related documents added (E09-T03). */
  readonly total: TotalCount;
  /** The documents matching the query (the base hits). */
  readonly hits: TotalCount;
  /** What Include added, by kind; null without Include. */
  readonly expanded: SearchExpandedCounts | null;
  readonly freshness: SearchFreshness;
  /** The freshness the result set was served with, read tolerantly (E16-T07). */
  readonly served: ServedFreshness;
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
 * - Selection (E16-T06): rows by document id (Space, Shift+Arrow, Shift+click, Ctrl+A for the page), or all results
 *   of the search, kept as its query and generation rather than ids. `selectionTarget` is what Mass Actions freeze.
 *   The count is announced; a page selection offers "Select all results" in a banner.
 * - Filter row (#191): one control per filterable column under the headers, compiled into the query language
 *   and ANDed with the keyword query, so a filtered list is an ordinary, audited, reproducible search. Arrows
 *   move between filters, Escape clears one; active filters stay listed above the grid as removable chips.
 */
@Component({
  selector: 'opp-review-grid',
  imports: [
    Button,
    CommandScopeDirective,
    EmptyState,
    ErrorState,
    FilterSummary,
    GridFilter,
    Icon,
    LoadingState,
    NgTemplateOutlet,
    ...MENU,
  ],
  providers: [ReviewSearchApi],
  templateUrl: './review-grid.html',
  styleUrls: ['./review-grid.scss', './review-grid-expansion.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewGrid implements CursorSource {
  readonly search = input.required<GridSearch>();
  readonly open = output<GridOpenEvent>();
  readonly selectionChange = output<ReadonlySet<string>>();
  /** The results were refreshed (Q-33): the server reopened its view, or the expired search ran again. */
  readonly refreshed = output<string>();
  /** A search ran and its first page is shown (or it found nothing). */
  readonly loaded = output<GridSearch>();

  private readonly api = inject(ReviewSearchApi);
  /** The reviewer's own saves search has not caught up with (E16-T05): their rows are marked until searchable. */
  protected readonly pendingCoding = inject(PendingCoding, { optional: true });
  /** The index's freshness (E16-T07): every served result set feeds it; the footnote reads it. */
  private readonly freshness = inject(FreshnessMonitor, { optional: true });
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly storage = inject(PreferenceStorage);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly filterRow = viewChild<ElementRef<HTMLElement>>('filterRow');
  private readonly filtersButton = viewChild('filtersButton', { read: ElementRef });
  private readonly summary = viewChild(FilterSummary);

  protected readonly canSearch = this.context.can(PERMISSIONS.searchExecute);
  protected readonly gridId = `grid-${this.context.workspaceId}`;

  // Columns
  private readonly fields = signal<FieldResource[] | null>(null);
  /** The workspace's field catalogue once loaded (Review mode's coding pane, E16-T05). */
  readonly fieldCatalogue = this.fields.asReadonly();
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

  // Filters (#191)
  protected readonly filtersOpen = computed(
    () => this.storage.read<boolean>(FILTER_ROW_KEY) === true,
  );
  /** The filter of each grid column, in column order (null: no control). */
  protected readonly filterCells = computed<(FilterSpec | null)[]>(() => {
    const byName = new Map((this.fields() ?? []).map((f) => [f.queryName.toLowerCase(), f]));
    const spec = (c: GridColumn) => filterSpec(byName.get(c.queryName), c.label, c.format);
    const { controlNumber, view } = this.columns();
    return [null, spec(controlNumber), null, ...view.map(spec)];
  });
  private readonly specs = computed(
    () => new Map(this.filterCells().flatMap((s) => (s ? [[s.queryName, s] as const] : []))),
  );
  private readonly _filters = signal<FilterSet>(new Map());
  /** Active filters by query name (saved searches, E07-T09). */
  readonly filters = this._filters.asReadonly();
  protected readonly filterErrors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly compiled = computed<CompiledQuery>(() => {
    const specs = this.specs();
    const clauses = [...this._filters()].flatMap(([queryName, value]) => {
      const spec = specs.get(queryName);
      const clause = spec && compileFilter(spec, value);
      return clause ? [{ queryName, clause }] : [];
    });
    return compileQuery(this.search().query, clauses);
  });
  /** The query the list shows: the keyword query ANDed with the filters. */
  readonly effectiveQuery = computed(() => this.compiled().query);
  protected readonly activeFilters = computed<ActiveFilter[]>(() => {
    const specs = this.specs();
    const errors = this.filterErrors();
    return [...this._filters()].map(([queryName, value]) => {
      const spec = specs.get(queryName);
      return {
        queryName,
        label: spec?.label ?? queryName,
        description: spec ? describeFilter(spec, value) : '',
        error: errors.get(queryName) ?? null,
      };
    });
  });
  /** The grid stays on screen while filters are active, even without results, so they can be changed. */
  protected readonly showTable = computed(() => {
    const status = this.status();
    return (
      status === 'ready' ||
      (this._filters().size > 0 && (status === 'empty' || status === 'invalid'))
    );
  });
  /** Rows above the data rows: the header, and the filter row when it is open. */
  protected readonly headRows = computed(() => (this.filtersOpen() ? 2 : 1));
  /** Last filter control that had focus: the row's single Tab stop. */
  private filterStop = 0;
  private readonly controlNumbers = new Map<string, string>();

  // Search state
  protected readonly status = signal<Status>('loading');
  protected readonly error = signal<ApiError | UserFacingError | null>(null);
  protected readonly window = signal(ResultWindow.EMPTY);
  /** The loaded rows in list order (the review cursor walks these, E16-T03). */
  readonly rows = computed(() => this.window().rows);
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
  /** The cursor page being fetched at either end, shared by scrolling and the review cursor. */
  private more: { direction: 'next' | 'previous'; promise: Promise<boolean> } | null = null;

  // Focus and selection
  protected readonly focusRow = signal(0);
  protected readonly focusCol = signal(COL_CONTROL);
  private readonly _selected = signal<ReadonlySet<string>>(new Set());
  /** Document ids of the rows checked one by one (empty while all results are selected). */
  readonly selection = this._selected.asReadonly();
  private readonly _allResults = signal<AllResultsSelection | null>(null);
  /** Every result of the search is selected, as its query and generation (E16-T06). */
  readonly allResults = this._allResults.asReadonly();
  /** What Mass Actions act on; null when nothing is selected. */
  readonly selectionTarget = computed<SelectionTarget | null>(() => {
    const all = this._allResults();
    if (all) return all;
    const ids = this._selected();
    return ids.size > 0 ? { kind: 'documents', documentIds: [...ids] } : null;
  });
  protected readonly selectedText = computed(() =>
    selectionLabel(this.selectionTarget(), this.prefs.locale()),
  );
  /** After "select this page": the banner offers all results when there are more than the page. */
  protected readonly offerAll = signal(false);

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
  readonly countText = computed(() => {
    const r = this.result();
    return r ? countLabel(r.total, r.freshness, this.prefs.locale()) : '';
  });
  /** With Include: "2 hits + 4 family, 1 duplicate, 1 email thread" next to the list size (base vs expanded counts). */
  protected readonly expandedText = computed(() => {
    const r = this.result();
    if (!r?.expanded) return null;
    const n = new Intl.NumberFormat(this.prefs.locale());
    const hits = countLabel(r.hits, r.freshness, this.prefs.locale());
    const parts = [
      [Number(r.expanded.family), 'family'],
      [
        Number(r.expanded.duplicates),
        Number(r.expanded.duplicates) === 1 ? 'duplicate' : 'duplicates',
      ],
      [Number(r.expanded.thread), 'email thread'],
    ]
      .filter(([count]) => Number(count) > 0)
      .map(([count, label]) => `${n.format(Number(count))} ${label}`);
    const hitWord = Number(r.hits.value) === 1 ? 'hit' : 'hits';
    return parts.length > 0
      ? `${hits} ${hitWord} + ${parts.join(', ')}`
      : `${hits} ${hitWord}, no related documents added`;
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
  /** "Counts may not include 85 recent changes." under a result set served while the index was catching up. */
  protected readonly footnote = computed(() => {
    const served = this.result()?.served;
    if (!served || served.state === 'current') return null;
    // The index has caught up since (a later reading; an older one never replaces the served state).
    const caughtUp = this.freshness?.index()?.state === 'current';
    return { text: footnoteText(served.pendingChanges, this.prefs.locale()), caughtUp };
  });
  protected readonly approximateTotal = computed(() => this.result()?.total.relation === 'gte');
  protected readonly ariaRowCount = computed(() => {
    const r = this.result();
    return r && r.total.relation === 'eq' ? Number(r.total.value) + this.headRows() : -1;
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
    if (this._allResults()) return { all: ids.length > 0, some: false };
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
    // Keys typed in the filter row belong to its controls (Space, Enter, Ctrl+A in a text box).
    const ready = { enabled: () => this.status() === 'ready' && !this.inFilterRow() };
    const onRow = {
      enabled: () => this.status() === 'ready' && this.focusRow() >= 0 && !this.inFilterRow(),
    };
    registry.handle('grid.openDocument', () => this.openFocused(), onRow);
    registry.handle('selection.toggleRow', () => this.toggleRow(this.focusRow()), onRow);
    registry.handle('selection.allOnPage', () => this.selectPage(true), ready);
    registry.handle('selection.allResults', () => this.selectAllResults(), ready);
    registry.handle('selection.clear', () => this.clearSelection(), ready);
    registry.handle('grid.toggleFilters', () => this.toggleFilters(true), {
      enabled: () => this.canSearch,
    });

    if (this.canSearch) {
      this.api.fields().then(
        (fields) => this.fields.set(fields),
        () => undefined, // the structural defaults stay
      );
    }

    effect(() => {
      if (this.search().deferred) return;
      untracked(() => this.run());
    });

    effect(() => {
      const served = this.result()?.served;
      if (served) untracked(() => this.freshness?.observe(served));
    });

    effect(() => {
      // Density changes the row height; the columns whether the grid scrolls sideways; the filter row the head.
      this.prefs.density();
      this.columns();
      this.status();
      this.filtersOpen();
      this.filterErrors();
      afterNextRender(
        () => {
          this.measure();
          this.syncFilterStops();
        },
        { injector: this.injector },
      );
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
  private async run(options: RunOptions = {}): Promise<void> {
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
    if (!this.showTable()) this.status.set('loading');
    this.busy.set(true);
    this.loadError.set(null);
    const sort = this.sort();
    const compiled = this.compiled();
    const all = this._allResults();
    const search = this.search();
    if (
      all &&
      (all.query !== compiled.query ||
        JSON.stringify(all.expand ?? null) !== JSON.stringify(search.expand ?? null))
    ) {
      // "All results" meant the results of the previous search; a new search does not inherit it.
      this.offerAll.set(false);
      this._allResults.set(null);
      this.selectionChange.emit(this._selected());
      this.announcer.announce('Selection of all results cleared: the search changed.');
    }
    // Filters narrow a saved search ad hoc: then the list runs the combined query text instead of the id.
    const unfiltered = compiled.query === search.query;
    const savedSearchId = search.savedSearchId && unfiltered ? search.savedSearchId : null;
    const termReport = search.termReport && unfiltered ? search.termReport : null;
    let page: SearchResultPage;
    try {
      page = await this.api.run({
        ...(termReport
          ? { searchTermReportId: termReport.reportId, termId: termReport.termId }
          : savedSearchId
            ? { savedSearchId }
            : { query: compiled.query }),
        sort: sort ? [sort] : null,
        ...(search.expand ? { expand: search.expand } : {}),
        countExact: this.countExact || null,
        pageSize: this.pageSize(),
        // The hits' snippets carry the search terms Review mode highlights in the extracted text (#130).
        highlight: true,
      });
    } catch (e) {
      if (seq !== this.seq) return;
      this.busy.set(false);
      this.fail(toApiError(e), compiled);
      return;
    }
    if (seq !== this.seq) return;
    this.busy.set(false);
    this.error.set(null);
    this.filterErrors.set(new Map());
    if (!page.searchId) {
      this.window.set(ResultWindow.EMPTY);
      this.result.set(null);
      this.status.set('not-indexed');
      return;
    }
    let window = ResultWindow.of(toLoadedPage(page), this.pageSize());
    const anchorPage = Math.ceil((options.anchorPosition ?? 0) / this.pageSize());
    if (options.anchor && window.indexOf(options.anchor) < 0 && anchorPage > 1 && page.nextCursor) {
      // The anchor was further down: load its page, so the list (and the review cursor) carries on from there.
      try {
        const there = await this.api.page(page.searchId, { page: anchorPage });
        if (seq !== this.seq) return;
        if (there.items.length > 0) window = ResultWindow.of(toLoadedPage(there), this.pageSize());
      } catch {
        if (seq !== this.seq) return; // the first page stays
      }
    }
    this.window.set(window);
    this.result.set(resultInfo(page.searchId, page));
    this.status.set(window.rows.length > 0 ? 'ready' : 'empty');
    this.loaded.emit(search);
    const anchor = options.anchor ? window.indexOf(options.anchor) : -1;
    // A sort started from a header keeps focus there.
    if (this.focusRow() !== -1) this.focusRow.set(Math.max(0, anchor));
    this.scrollTo(Math.max(0, anchor), anchor >= 0 ? 'nearest' : 'start');
    const notice = options.notice ?? (page.resultsRefreshed ? REFRESHED : null);
    this.notice.set(notice);
    this.announcer.announce(notice ?? `${this.countText()} documents`);
    if (notice) this.refreshed.emit(notice);
    if (options.narrowed) void this.pruneSelection(window, compiled, seq);
  }

  private fail(error: ApiError, compiled: CompiledQuery): void {
    this.window.set(ResultWindow.EMPTY);
    this.result.set(null);
    if (error.code === 'invalid-query' && compiled.clauses.length > 0) {
      // Messages about a filter's clause go under that filter; the grid stays so it can be corrected.
      const errors = (error.problem['queryErrors'] as QueryError[] | undefined) ?? [];
      const byFilter = new Map<string, string>();
      let other = false;
      for (const e of errors) {
        const queryName = clauseAt(compiled, Number(e.span?.start ?? -1));
        if (!queryName) other = true;
        else if (!byFilter.has(queryName))
          byFilter.set(queryName, e.message ?? 'This filter cannot be applied.');
      }
      if (byFilter.size > 0 && !other) {
        this.filterErrors.set(byFilter);
        this.status.set('invalid');
        const labels = [...byFilter.keys()].map((q) => this.specs().get(q)?.label ?? q);
        this.announcer.announce(`The ${labels.join(', ')} filter cannot be applied.`, {
          politeness: 'assertive',
        });
        return;
      }
    }
    this.filterErrors.set(new Map());
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

  /** The index caught up after these results were served: run the search again, keeping the focused document. */
  protected showRecentChanges(): void {
    const row = this.focusRow();
    void this.run({
      anchor: this.focusedId(),
      anchorPosition: row >= 0 ? this.window().position(row) : null,
    });
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

  /** Header click or Enter on a header: ascending → descending → back to the default order (relevance with a keyword, else Control Number). */
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
  private loadMore(direction: 'next' | 'previous'): Promise<boolean> {
    if (this.more)
      return this.more.direction === direction ? this.more.promise : Promise.resolve(false);
    const promise = this.fetchPage(direction).finally(() => (this.more = null));
    this.more = { direction, promise };
    return promise;
  }

  private async fetchPage(direction: 'next' | 'previous'): Promise<boolean> {
    const window = this.window();
    const result = this.result();
    const cursor = direction === 'next' ? window.last?.next : window.first?.previous;
    if (!cursor || !result || this.busy()) return false;
    const seq = this.seq;
    this.loadingMore.set(direction);
    this.loadError.set(null);
    let page: SearchResultPage;
    try {
      page = await this.api.page(result.searchId, { cursor });
    } catch (e) {
      if (seq !== this.seq) return false;
      this.loadingMore.set(null);
      const error = toApiError(e);
      if (error.status === 404) {
        await this.runExpired();
        return false;
      }
      this.pageFailed(error);
      return false;
    }
    this.loadingMore.set(null);
    if (seq !== this.seq || this.window() !== window) return false;
    const loaded = toLoadedPage(page);
    this.result.set(resultInfo(result.searchId, page));
    if (page.resultsRefreshed) this.onRefreshed();
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
    if (page.resultsRefreshed) this.onRefreshed();
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

  /** The search expired (idle timeout) or its handle is gone: run it again, back at the focused row (Q-33). */
  private runExpired(): Promise<void> {
    const row = this.focusRow();
    return this.run({
      anchor: this.focusedId(),
      anchorPosition: row >= 0 ? this.window().position(row) : null,
      notice: EXPIRED,
    });
  }

  private pageFailed(error: ApiError): void {
    if (error.status === 404) {
      void this.runExpired();
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

  private onRefreshed(): void {
    this.notice.set(REFRESHED);
    this.announcer.announce(REFRESHED);
    this.refreshed.emit(REFRESHED);
  }

  // ── Filters (#191) ───────────────────────────────────────────────────────────────────────────────────────

  /** Shows or hides the filter row (toolbar button, command); the command moves focus into the row. */
  toggleFilters(focus = false): void {
    const open = !this.filtersOpen();
    const hadFocus = this.inFilterRow();
    this.storage.write(FILTER_ROW_KEY, open);
    afterNextRender(
      () => {
        if (open && focus) this.filterStops()[0]?.focus();
        else if (!open && hadFocus) this.viewport()?.nativeElement.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** A filter control changed: run the search again (sort kept, from the first page). */
  protected onFilterChange(queryName: string, change: FilterChange): void {
    this.setFilter(queryName, change.value);
  }

  setFilter(queryName: string, value: FilterValue | null): void {
    const current = this._filters();
    if (sameValue(current.get(queryName) ?? null, value)) return;
    const next = new Map(current);
    if (value) next.set(queryName, value);
    else next.delete(queryName);
    this._filters.set(next);
    this.filterErrors.update((e) => {
      const rest = new Map(e);
      rest.delete(queryName);
      return rest;
    });
    void this.run({ narrowed: value !== null });
  }

  protected removeFilter(queryName: string): void {
    const index = [...this._filters().keys()].indexOf(queryName);
    this.setFilter(queryName, null);
    this.afterFilterRemoval(index);
  }

  clearFilters(): void {
    if (this._filters().size === 0) return;
    this._filters.set(new Map());
    this.filterErrors.set(new Map());
    void this.run();
    this.afterFilterRemoval(-1);
  }

  /** Drops the filters without running or moving focus: a new search (a saved search) is about to replace them. */
  resetFilters(): void {
    if (this._filters().size === 0) return;
    this._filters.set(new Map());
    this.filterErrors.set(new Map());
  }

  /** Keeps focus near a removed chip: the next chip, else the Filters button, else the list. */
  private afterFilterRemoval(index: number): void {
    afterNextRender(
      () => {
        if (index >= 0 && this._filters().size > 0) this.summary()?.focusChip(index);
        else (this.filtersButton() ?? this.viewport())?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  private inFilterRow(): boolean {
    return !!this.document.activeElement?.closest('.grid__row--filters');
  }

  private filterStops(): HTMLElement[] {
    const row = this.filterRow()?.nativeElement;
    return row ? [...row.querySelectorAll<HTMLElement>('[data-filter-stop]')] : [];
  }

  /** One Tab stop in the filter row (roving tabindex): the control used last. */
  private syncFilterStops(): void {
    const stops = this.filterStops();
    const current = Math.min(this.filterStop, stops.length - 1);
    stops.forEach((el, i) => (el.tabIndex = i === current ? 0 : -1));
  }

  protected onFilterFocus(event: FocusEvent): void {
    const index = this.filterStops().indexOf(event.target as HTMLElement);
    if (index < 0) return;
    this.filterStop = index;
    this.syncFilterStops();
  }

  /** Left/Right (at the ends of a text box) and Home/End move between the filter controls. */
  protected onFilterKeydown(event: KeyboardEvent): void {
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const target = event.target as HTMLElement;
    const stops = this.filterStops();
    const index = stops.indexOf(target);
    if (index < 0) return;
    const text = target instanceof HTMLInputElement ? target : null;
    let next: number;
    switch (event.key) {
      case 'ArrowLeft':
        if (text && (text.selectionStart !== 0 || text.selectionEnd !== 0)) return;
        next = index - 1;
        break;
      case 'ArrowRight':
        if (text && text.selectionStart !== text.value.length) return;
        next = index + 1;
        break;
      case 'Home':
      case 'End':
        if (text) return;
        next = event.key === 'Home' ? 0 : stops.length - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    stops[Math.max(0, Math.min(stops.length - 1, next))].focus();
  }

  /**
   * After a filter was added or changed, deselects documents no longer in the results: by the loaded rows when
   * they are the whole result, otherwise by asking the server which selected documents still match.
   */
  private async pruneSelection(
    window: ResultWindow,
    compiled: CompiledQuery,
    seq: number,
  ): Promise<void> {
    const shown = new Set(window.rows.map((r) => r.documentId));
    const unknown = [...this.selection()].filter((id) => !shown.has(id));
    if (unknown.length === 0) return;
    let gone: string[];
    if (!window.hasNext && !window.hasPrevious) {
      gone = unknown;
    } else {
      gone = [];
      const checkable = unknown.filter((id) => this.controlNumbers.has(id));
      for (let i = 0; i < checkable.length; i += VERIFY_CHUNK) {
        const ids = checkable.slice(i, i + VERIFY_CHUNK);
        const numbers = ids.map((id) => quotePhrase(this.controlNumbers.get(id)!)).join(' OR ');
        const clause = { queryName: 'controlnumber', clause: `controlnumber:(${numbers})` };
        const query = compileQuery(compiled.query, [clause]).query;
        let found: Set<string>;
        try {
          const page = await this.api.run({ query, pageSize: ids.length, highlight: false });
          found = new Set(page.items.map((h) => h.documentId));
        } catch {
          return; // Unknown: the selection stays as it is.
        }
        if (seq !== this.seq) return;
        gone.push(...ids.filter((id) => !found.has(id)));
      }
    }
    if (seq !== this.seq || gone.length === 0) return;
    const next = new Set(this.selection());
    for (const id of gone) next.delete(id);
    this.setSelection(next);
    const n = gone.length;
    this.announcer.announce(
      n === 1
        ? '1 selected document is not in the filtered results and was deselected.'
        : `${n} selected documents are not in the filtered results and were deselected.`,
    );
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
    // Hidden while Review mode is open: keep the last layout (and scroll row) for the way back.
    if (!el || el.closest('[hidden]')) return;
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
    // Keys typed in the filter row (inside the grid element) are the filters' own.
    if (event.target !== event.currentTarget) return;
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

  // ── Review cursor source (E16-T03) ───────────────────────────────────────────────────────────────────────

  /** 1-based position of loaded row `index` in the whole result, when known. */
  positionOf(index: number): number | null {
    return this.window().position(index);
  }

  /** More rows can be fetched beyond the loaded ones in `direction`. */
  hasMore(direction: 'next' | 'previous'): boolean {
    const w = this.window();
    return direction === 'next' ? w.hasNext : w.hasPrevious;
  }

  /** Fetches the neighbouring cursor page; true when it was added. An expired search runs again (new rows). */
  fetchMore(direction: 'next' | 'previous'): Promise<boolean> {
    return this.status() === 'ready' ? this.loadMore(direction) : Promise.resolve(false);
  }

  /**
   * Makes `documentId` the focused row, scrolled into view when the list is shown again (the review cursor
   * keeps the hidden list in step; it fetches pages itself).
   */
  focusDocument(documentId: string): void {
    const index = this.window().indexOf(documentId);
    if (index < 0) return;
    this.focusRow.set(index);
    const top = this.scrollRow();
    const visible = this.visibleCount();
    if (index < top) this.scrollRow.set(index);
    else if (index > top + visible - 1) this.scrollRow.set(index - visible + 1);
  }

  /** Back from Review mode: the list scrolls to where it was and takes focus; the selection never changed. */
  restoreView(): void {
    afterNextRender(
      {
        write: () => {
          const el = this.viewport()?.nativeElement;
          if (!el) return;
          el.scrollTop = this.scrollRow() * this.rowHeight();
          this.measure();
          el.focus({ preventScroll: true });
          this.maybeLoadMore();
        },
      },
      { injector: this.injector },
    );
  }

  // ── Selection ────────────────────────────────────────────────────────────────────────────────────────────

  private anchor = -1;

  protected isSelected(hit: SearchHit): boolean {
    return this._allResults() !== null || this.selection().has(hit.documentId);
  }

  /** Space or a checkbox click; Shift+click checks the rows between the last toggled row and this one. */
  protected toggleRow(index: number, range = false): void {
    const hit = this.rows()[index];
    if (!hit) return;
    const next = new Set(this.rowSelection());
    if (range && this.anchor >= 0 && this.anchor !== index) {
      for (const i of rangeBetween(this.anchor, index)) {
        const row = this.rows()[i];
        if (row) next.add(row.documentId);
      }
    } else if (next.has(hit.documentId)) next.delete(hit.documentId);
    else next.add(hit.documentId);
    this.anchor = index;
    this.setSelection(next);
  }

  private extendSelection(from: number, to: number): void {
    if (this._allResults()) return; // every row is selected already
    const rows = this.rows();
    const next = new Set(this.selection());
    for (const i of [from, to]) if (rows[i]) next.add(rows[i].documentId);
    this.anchor = to;
    this.setSelection(next);
  }

  /** Checks (or unchecks) the rows of the page in view (Ctrl+A, the header checkbox, Select › This page). */
  protected selectPage(select: boolean): void {
    const next = new Set(this.rowSelection());
    for (const hit of this.pageRows(this.currentPage())) {
      if (select) next.add(hit.documentId);
      else next.delete(hit.documentId);
    }
    this.setSelection(next);
    const w = this.window();
    this.offerAll.set(
      select && !this.termHits() && (w.hasNext || w.hasPrevious || w.pages.length > 1),
    );
  }

  /** Select › This page: the page in view and nothing else. */
  protected selectOnlyPage(): void {
    this._allResults.set(null);
    this._selected.set(new Set());
    this.selectPage(true);
  }

  /** The list shows a report term's hits in its frozen set (not narrowed by filters). */
  private termHits(): boolean {
    return !!this.search().termReport && this.compiled().query === this.search().query;
  }

  /** Selects every result of the search, loaded or not, as the query and the generation it was served at. */
  selectAllResults(): void {
    const r = this.result();
    if (!r || this.status() !== 'ready') return;
    if (this.termHits()) {
      // A report term's hits are a frozen set that no query names, so they cannot be selected as "all results".
      this.offerAll.set(false);
      this.announcer.announce(
        'All results of a Search Terms Report term cannot be selected. Select documents on the loaded pages.',
      );
      return;
    }
    this._selected.set(new Set());
    this.controlNumbers.clear();
    this.offerAll.set(false);
    this._allResults.set({
      kind: 'all',
      query: this.compiled().query,
      generation:
        r.freshness.servedGeneration === null ? null : String(r.freshness.servedGeneration),
      total: r.total,
      countText: this.countText(),
      expand: this.search().expand ?? null,
    });
    this.selectionChange.emit(this._selected());
    this.announceSelection();
  }

  /** The rows selected one by one; leaving "all results" keeps the loaded rows selected, so a row can be unchecked. */
  private rowSelection(): ReadonlySet<string> {
    if (!this._allResults()) return this.selection();
    this._allResults.set(null);
    return new Set(this.rows().map((r) => r.documentId));
  }

  private setSelection(next: ReadonlySet<string>): void {
    // Control numbers of selected rows, to check them against a filtered result later.
    for (const row of this.rows()) {
      if (next.has(row.documentId)) this.controlNumbers.set(row.documentId, row.controlNumber);
    }
    for (const id of this.controlNumbers.keys()) if (!next.has(id)) this.controlNumbers.delete(id);
    if (next.size === 0) this._allResults.set(null);
    if (this._allResults() === null) this.offerAll.set(false);
    this._selected.set(next);
    this.selectionChange.emit(next);
    this.announceSelection();
  }

  private announceSelection(): void {
    // One message per burst (Shift+Arrow held down), the latest count.
    this.announcer.announce(selectionAnnouncement(this.selectionTarget(), this.prefs.locale()), {
      throttleKey: `${this.gridId}-selection`,
      minIntervalMs: 600,
    });
  }

  /** Clears the selection (Select › None, Alt+Shift+0, Mass Actions after a run). */
  clearSelection(): void {
    this._allResults.set(null);
    this.offerAll.set(false);
    this.setSelection(new Set());
  }

  /** Puts keyboard focus on the list (focus returns here after Mass Actions). */
  focus(): void {
    this.viewport()?.nativeElement.focus({ preventScroll: true });
  }

  /** The list element, for dialogs that return focus to it. */
  focusTarget(): HTMLElement | undefined {
    return this.viewport()?.nativeElement;
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
    // The header (and the filter row) come first. Without a known position (end of an inexact result) rows are
    // numbered as loaded.
    return (position ?? index + 1) + this.headRows();
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
  protected readonly relatedTag = relatedTag;
  protected readonly COL_SELECT = COL_SELECT;
  protected readonly COL_CONTROL = COL_CONTROL;
  protected readonly FIXED_COLUMNS = FIXED_COLUMNS;
  protected readonly PAGE_SIZES = PAGE_SIZES;
}

/** The tag of a row Include added (E09-T03): why it is in the list although it does not match the query. */
function relatedTag(hit: SearchHit): { text: string; label: string } | null {
  switch (hit.expandedBy) {
    case 'family':
      return { text: 'Family', label: 'Added as family' };
    case 'duplicate':
      return { text: 'Duplicate', label: 'Added as duplicate' };
    case 'thread':
      return { text: 'Thread', label: 'Added from the email thread' };
    default:
      return null;
  }
}

const REFRESHED = 'Results refreshed: the list now includes recent changes.';
const EXPIRED = 'Results refreshed: the search had expired and was run again.';

/** `b` directly follows `a` (both numbered). */
function adjacent(a: LoadedPage | undefined, b: LoadedPage | undefined): boolean {
  return !!a && !!b && a.number !== null && b.number === a.number + 1;
}

function sameValue(a: FilterValue | null, b: FilterValue | null): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function resultInfo(searchId: string, page: SearchResultPage): ResultInfo {
  const expanded = page.expanded ?? null;
  return {
    searchId,
    total: expanded ? { value: expanded.total, relation: 'eq' } : page.total,
    hits: page.total,
    expanded,
    freshness: page.freshness,
    served: toServedFreshness(page.freshness),
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
