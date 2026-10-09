import { Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom, map } from 'rxjs';
import { toApiError } from '../../core/api/problem-details';
import { CommandRegionDirective, CommandRegistry } from '../../core/commands';
import { PreferenceStorage } from '../../core/preferences/preference-storage';
import { SessionService } from '../../core/session/session';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { Badge, Button, DialogService, Icon, IconButton, ToastService } from '../../ui';
import {
  HttpSavedSearchApi,
  SavedSearch,
  SavedSearchApi,
  SavedSearchSummary,
} from '../searches/saved-search-api';
import type { SavedSearchDialogData } from '../searches/saved-search-dialog';
import {
  FILTERED_NOTICE,
  LIVE_LABEL,
  SAVED_SEARCH_PARAM,
  THEN_PARAM,
  savedSearchErrorText,
} from '../searches/saved-search-model';
import { SavedSearchBrowser } from './browser/saved-search-browser';
import {
  GridOpenEvent,
  GridSearch,
  RelationshipPivot,
  ReviewGrid,
  pivotLabel,
  pivotQuery,
} from './grid/review-grid';
import { HttpRelationshipsApi, RelationshipsApi } from './relationships/relationships-api';
import {
  RELATED_ID_PARAM,
  RELATED_OF_PARAM,
  RELATED_PARAM,
  relatedFromParams,
} from './relationships/relationship-link';
import { PendingCoding } from './review/coding/pending-coding';
import { DocumentAccess } from './review/document-access';
import { DocumentLoader } from './review/document-loader';
import { CursorSource, ReviewCursor } from './review/review-cursor';
import {
  CodingApi,
  DocumentContentApi,
  HttpCodingApi,
  HttpDocumentContentApi,
} from './review/review-ports';
import { ReviewWorkspace } from './review/review-workspace';
import { HttpRedactionApi, RedactionApi } from './review/viewer/redaction/redaction-api';
import { QueryBar, QuerySubmission } from './search/query-bar';
import {
  IncludeRelated,
  IncludeRelatedToggles,
  NO_RELATED,
  includeOf,
  toExpand,
} from './search/include-related';
import { BulkCodingApi, HttpBulkCodingApi } from './mass-edit/bulk-coding-api';
import { MassActions } from './mass-edit/mass-actions';
import { MassEditJobs } from './mass-edit/mass-edit-jobs';
import { FreshnessMonitor } from './freshness/freshness-monitor';
import { FreshnessStatus } from './freshness/freshness-status';
import { SearchJobBanner } from './freshness/job-banner';
import { PendingSearchJobs } from './freshness/pending-jobs';
import { HttpSearchFreshnessApi, SearchFreshnessApi } from '../../core/search/search-freshness';
import {
  HttpSearchTermReportApi,
  ReportTerm,
  SearchTermReport,
  SearchTermReportApi,
} from '../searches/terms-reports/search-term-report-api';
import {
  TERM_PARAM,
  TERM_REPORT_PARAM,
  reportErrorText,
} from '../searches/terms-reports/search-term-report-model';
import {
  HighlightSetsApi,
  HighlightState,
  HttpHighlightSetsApi,
} from '../../core/highlights/highlight-sets';

/**
 * Documents: the default landing page of a workspace (familiarity guide §2.1, §3), with two modes on one route.
 * List mode: the search panel with the keyword query bar (E16-T01) above the document list (E16-T02). The list
 * opens on every document (an empty query) and shows the results of each search. Review mode (E16-T03): Enter,
 * double-click on a row opens it in the review workspace, with the review cursor bound to the list.
 *
 * The list stays alive (hidden) while reviewing, so its loaded pages, sort, filters and selection are the ones
 * the cursor walks, and "Back to list" returns to the same scroll position and selection, with the reviewed
 * document focused.
 *
 * Review mode is a browser history entry (`?view=review`): the browser's Back button returns to the list like
 * "Back to list" (asking about unsaved edits first), and Forward reopens the last reviewed document.
 *
 * Mass Actions (E16-T06) sit in the list's header and act on its selection (Mass Edit: bulk coding job).
 *
 * Search freshness (E16-T07, Q-10): the list's header carries the freshness pill (Current / Updating / Delayed, details
 * with raw generations for admins and support only), each result set its plain-language stamp and, when not current,
 * the footnote "Counts may not include N recent changes"; a banner follows the reviewer's own saved jobs (Mass Edit,
 * imports, overlays) until they are searchable.
 *
 * Saved searches (E16-T11): the browser pane on the left lists them for quick running and offers "Save current
 * search"; `?savedSearch=<id>` runs one (by id, so the server re-parses it and records the run) and the search panel
 * names it. `&then=massEdit` then selects all its results and opens Mass Edit (the Searches section's "Mass Edit
 * results").
 *
 * Search Terms Reports (#180): `?termReport=<reportId>&term=<termId>` lists one term's hits within the report's frozen
 * set (filtered for the caller); the search panel names the report and the term, and says when filters turn it into a
 * live search of the term's expression.
 *
 * Families and duplicate groups (E13-T02 "Open in Documents"): `?related=family|duplicates&relatedId=&relatedOf=` shows
 * the relation as the list's search, like the duplicate marker does.
 *
 * Keyboard (guide §4, command registry E15-T03): "Focus keyword search" (Alt+Shift+K, or `/` while single-key
 * shortcuts are on) and the region cycle (Alt+Shift+G / Alt+Shift+B) between the search panel, the list and the
 * saved searches, or between the review panes.
 */
const REVIEW_PARAM = 'view';
const REVIEW_VALUE = 'review';
/** Whether the browser pane is open (a user preference). */
const BROWSER_KEY = 'pane.documentsBrowser';

@Component({
  selector: 'opp-documents-page',
  imports: [
    Badge,
    Button,
    CommandRegionDirective,
    FreshnessStatus,
    Icon,
    IconButton,
    IncludeRelatedToggles,
    MassActions,
    QueryBar,
    ReviewGrid,
    ReviewWorkspace,
    RouterLink,
    SavedSearchBrowser,
    SearchJobBanner,
  ],
  template: `<h1 class="documents__title" [class.opp-visually-hidden]="reviewing()">Documents</h1>
    <div class="documents__list" [class.is-browser-closed]="!browserOpen()" [hidden]="reviewing()">
      @if (browserOpen()) {
        <aside
          id="documents-browser"
          class="documents__browser"
          aria-labelledby="documents-browser-heading"
          oppCommandRegion
        >
          <opp-saved-search-browser
            headingId="documents-browser-heading"
            [activeId]="saved()?.savedSearchId ?? null"
            (run)="runSaved($event)"
            (save)="saveCurrent()"
          />
          <button
            type="button"
            oppButton="ghost"
            class="documents__browser-toggle"
            aria-controls="documents-browser"
            aria-expanded="true"
            (click)="toggleBrowser()"
          >
            <opp-icon name="sidebar-collapse" />
            Hide saved searches
          </button>
        </aside>
      } @else {
        <div class="documents__browser-closed">
          <button
            type="button"
            oppIconButton
            label="Show saved searches"
            aria-expanded="false"
            (click)="toggleBrowser()"
          >
            <opp-icon name="sidebar-expand" />
          </button>
        </div>
      }
      <div class="documents__main">
        <section
          class="documents__search"
          aria-labelledby="documents-search-heading"
          oppCommandRegion
        >
          <h2 id="documents-search-heading" class="opp-visually-hidden">Search</h2>
          @if (saved(); as s) {
            <div class="documents__saved" role="group" aria-label="Saved search">
              <opp-badge tone="info" icon="refresh">{{ liveLabel }}</opp-badge>
              <span class="opp-visually-hidden">: </span>
              <strong>{{ s.name }}</strong>
              @if (s.owner.userId !== myId()) {
                <span class="documents__saved-note">
                  Shared by {{ s.owner.displayName }}. {{ filteredNotice }}
                </span>
              }
              <button type="button" oppButton="ghost" (click)="clearSaved()">
                <opp-icon name="close" />
                Clear saved search
              </button>
            </div>
          }
          @if (termView(); as tv) {
            <div class="documents__saved" role="group" aria-label="Search Terms Report term">
              <opp-badge tone="neutral" icon="report">Search Terms Report</opp-badge>
              <span class="opp-visually-hidden">: </span>
              <strong>{{ tv.report.name }}</strong>
              <span aria-hidden="true">›</span>
              <span class="opp-visually-hidden">, term </span>
              <strong>{{ tv.term.name }}</strong>
              <span class="documents__saved-note">
                @if (termFiltered()) {
                  With your filters this is a live search of the term's expression in the whole
                  workspace, not the report's frozen set.
                } @else {
                  Documents of the report's frozen set that match this term. {{ filteredNotice }}
                }
              </span>
              <a
                oppButton="ghost"
                [routerLink]="['/w', workspaceId, 'searches', 'terms-reports', tv.report.reportId]"
              >
                <opp-icon name="report" />
                Back to report
              </a>
              <button type="button" oppButton="ghost" (click)="clearTerm()">
                <opp-icon name="close" />
                Clear term
              </button>
            </div>
          }
          <opp-query-bar (search)="onSearch($event)" />
          <opp-include-related [value]="include()" (valueChange)="onInclude($event)" />
        </section>
        <opp-search-job-banner />
        <section aria-labelledby="documents-list-heading" oppCommandRegion>
          <h2 id="documents-list-heading" class="opp-visually-hidden">Document list</h2>
          <opp-review-grid
            [search]="search()"
            (open)="onOpen($event)"
            (refreshed)="onRefreshed($event)"
            (loaded)="onLoaded($event)"
            (pivot)="onPivot($event)"
          >
            <opp-freshness-status gridFreshness />
            <opp-mass-actions
              gridActions
              [target]="grid().selectionTarget()"
              [listCount]="grid().countText()"
              [fields]="grid().fieldCatalogue()"
              [returnFocus]="listElement"
            />
          </opp-review-grid>
        </section>
      </div>
    </div>
    @if (reviewing()) {
      <opp-review-workspace
        [cursor]="cursor"
        [fields]="grid().fieldCatalogue()"
        (back)="closeReview()"
        (pivot)="onPivot($event)"
      />
    }`,
  styleUrl: './documents-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    DocumentAccess,
    DocumentLoader,
    { provide: DocumentContentApi, useClass: HttpDocumentContentApi },
    { provide: CodingApi, useClass: HttpCodingApi },
    { provide: RelationshipsApi, useClass: HttpRelationshipsApi },
    { provide: RedactionApi, useClass: HttpRedactionApi },
    PendingCoding,
    { provide: BulkCodingApi, useClass: HttpBulkCodingApi },
    MassEditJobs,
    { provide: SearchFreshnessApi, useClass: HttpSearchFreshnessApi },
    FreshnessMonitor,
    PendingSearchJobs,
    { provide: SavedSearchApi, useClass: HttpSavedSearchApi },
    { provide: SearchTermReportApi, useClass: HttpSearchTermReportApi },
    { provide: HighlightSetsApi, useClass: HttpHighlightSetsApi },
    HighlightState,
  ],
  host: { '[class.is-reviewing]': 'reviewing()' },
})
export class DocumentsPage {
  /** A link to a saved search runs only that search, not every document first. */
  protected readonly search = signal<GridSearch>({
    query: '',
    deferred:
      inject(ActivatedRoute).snapshot.queryParamMap.has(SAVED_SEARCH_PARAM) ||
      inject(ActivatedRoute).snapshot.queryParamMap.has(TERM_REPORT_PARAM) ||
      !!relatedFromParams(inject(ActivatedRoute).snapshot.queryParamMap),
  });
  protected readonly reviewing = signal(false);
  private readonly queryBar = viewChild.required(QueryBar);
  protected readonly grid = viewChild.required(ReviewGrid);
  private readonly review = viewChild(ReviewWorkspace);
  private readonly massActions = viewChild(MassActions);
  private readonly browser = viewChild(SavedSearchBrowser);

  /** The list as the cursor sees it (the grid is a view child, so it is read lazily). */
  private readonly source: CursorSource = {
    rows: computed(() => this.grid().rows()),
    countText: computed(() => this.grid().countText()),
    positionOf: (index) => this.grid().positionOf(index),
    hasMore: (direction) => this.grid().hasMore(direction),
    fetchMore: (direction) => this.grid().fetchMore(direction),
    searchId: computed(() => this.grid().searchId()),
  };
  protected readonly cursor = new ReviewCursor(this.source);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly location = inject(Location);
  /** `?view=review` in the URL: the history entry Review mode adds on top of the list. */
  private readonly reviewInUrl = toSignal(
    this.route.queryParamMap.pipe(map((p) => p.get(REVIEW_PARAM) === REVIEW_VALUE)),
    { initialValue: false },
  );
  /** Mass Actions dialogs return focus to the list. */
  protected readonly listElement = () => this.grid().focusTarget();

  // Saved searches (E16-T11)
  private readonly savedApi = inject(SavedSearchApi);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly injector = inject(Injector);
  private readonly storage = inject(PreferenceStorage);
  private readonly principal = inject(SessionService).principal;
  protected readonly myId = computed(() => this.principal()?.userId ?? null);
  protected readonly liveLabel = LIVE_LABEL;
  protected readonly filteredNotice = FILTERED_NOTICE;
  /** "Include: Family / Duplicates / Email thread" of the list (E09-T03); a saved search sets its stored choice. */
  protected readonly include = signal<IncludeRelated>(NO_RELATED);
  /** The saved search the list shows (run from the browser pane, a link, or just saved). */
  protected readonly saved = signal<SavedSearch | null>(null);
  /** The `?savedSearch=` id applied (or being applied), so the URL echo of our own change is ignored. */
  private appliedId: string | null = null;
  /** `?then=massEdit`: open Mass Edit over all results once this saved search has run. */
  private pendingMassEdit: string | null = null;
  private readonly params = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });
  // Search Terms Report terms (#180)
  private readonly termApi = inject(SearchTermReportApi);
  protected readonly workspaceId = inject(WorkspaceContext).workspaceId;
  /** The report term the list shows (`?termReport=&term=`). */
  protected readonly termView = signal<{ report: SearchTermReport; term: ReportTerm } | null>(null);
  /** `reportId/termId` applied (or being applied), so the URL echo of our own change is ignored. */
  private appliedTerm: string | null = null;
  /** Filters (or another query) narrow the term: the list runs its expression live. */
  protected readonly termFiltered = computed(() => {
    const tv = this.termView();
    return !!tv && this.grid().effectiveQuery() !== tv.term.expression;
  });

  protected readonly browserOpen = linkedSignal(
    () => this.storage.read<{ open?: boolean }>(BROWSER_KEY)?.open !== false,
  );

  constructor() {
    inject(CommandRegistry).handle('search.focus', () => this.queryBar().focus(), {
      enabled: () => !this.reviewing(),
    });
    // `?savedSearch=<id>` (a link from Searches, the browser pane, Back/Forward) runs that saved search.
    effect(() => {
      const params = this.params();
      const id = params.get(SAVED_SEARCH_PARAM);
      const then = params.get(THEN_PARAM);
      const reportId = params.get(TERM_REPORT_PARAM);
      const termId = params.get(TERM_PARAM);
      const related = relatedFromParams(params);
      untracked(() => {
        // `?related=family|duplicates&relatedId=&relatedOf=` ("Open in Documents" from another section, E13-T02): the
        // relation becomes the list's search once the view exists, and the parameters leave the URL.
        if (related) {
          queueMicrotask(() => this.onPivot(related));
          void this.router.navigate([], {
            queryParams: {
              [RELATED_PARAM]: null,
              [RELATED_ID_PARAM]: null,
              [RELATED_OF_PARAM]: null,
            },
            queryParamsHandling: 'merge',
            replaceUrl: true,
          });
          return;
        }
        const termKey = reportId && termId ? `${reportId}/${termId}` : null;
        if (termKey && termKey !== this.appliedTerm) {
          void this.applyTerm(reportId!, termId!);
          return;
        }
        if (!termKey && this.appliedTerm) {
          // Back from a report term to the plain list (or to a saved search, applied below).
          this.termView.set(null);
          this.appliedTerm = null;
          if (!id) {
            this.queryBar().clear();
            this.search.set({ query: '' });
          }
        }
        if (termKey) return;
        if (id && then === 'massEdit') this.pendingMassEdit = id;
        if (id && id !== this.appliedId) void this.applySaved(id);
        else if (!id && this.appliedId) {
          // Back from a saved search to the plain list.
          this.saved.set(null);
          this.appliedId = null;
          this.queryBar().clear();
          this.search.set(this.searchFor(''));
        }
      });
    });
    // The list follows the cursor while it is hidden, so it comes back on the reviewed document.
    effect(() => {
      const id = this.cursor.documentId();
      if (id && this.reviewing()) untracked(() => this.grid().focusDocument(id));
    });
    // Browser Back/Forward across the Review mode history entry.
    let wasInUrl = false;
    effect(() => {
      const inUrl = this.reviewInUrl();
      const changed = inUrl !== wasInUrl;
      wasInUrl = inUrl;
      untracked(() => {
        if (!inUrl && changed && this.reviewing()) void this.onBrowserBack();
        else if (inUrl && !this.reviewing()) this.onReviewEntry();
      });
    });
  }

  protected onSearch(submission: QuerySubmission): void {
    const tv = this.termView();
    if (tv) {
      if (submission.query === tv.term.expression) {
        this.search.set(this.termSearch(tv.report, tv.term));
        return;
      }
      this.forgetTerm();
      this.search.set({ query: submission.query });
      return;
    }
    const saved = this.saved();
    if (saved && submission.query === saved.query) {
      // Searching the saved query again keeps it a run of the saved search.
      this.search.set(this.searchFor(saved.query, saved.savedSearchId));
      return;
    }
    if (saved) this.forgetSaved();
    this.search.set(this.searchFor(submission.query));
  }

  /** The Include toggles changed: the list runs again with the new expansion. */
  protected onInclude(value: IncludeRelated): void {
    this.include.set(value);
    const search = this.search();
    if (search.deferred) return;
    this.search.set(this.searchFor(search.query, search.savedSearchId ?? null));
  }

  /** What the list runs: a query (or a saved search by id) with the Include choice, always explicit for a saved search. */
  private searchFor(query: string, savedSearchId: string | null = null): GridSearch {
    const expand = toExpand(this.include(), savedSearchId !== null);
    return savedSearchId ? { query, savedSearchId, expand } : { query, expand };
  }

  // ── Search Terms Report terms ───────────────────────────────────────────────────────────────────────────────

  /** Applies `?termReport=&term=`: loads the report, shows the report and term names and lists the term's hits. */
  private async applyTerm(reportId: string, termId: string): Promise<void> {
    const key = `${reportId}/${termId}`;
    this.appliedTerm = key;
    let report: SearchTermReport;
    try {
      report = await this.termApi.get(reportId);
    } catch (e) {
      if (this.appliedTerm !== key) return;
      this.dropTerm(reportErrorText(toApiError(e)));
      return;
    }
    if (this.appliedTerm !== key) return;
    const term = report.terms.find((t) => t.termId === termId);
    if (!term || term.error || report.status !== 'completed') {
      this.dropTerm(
        !term
          ? 'This term is not part of the report.'
          : term.error
            ? 'This term has a syntax error, so it has no documents to show.'
            : 'The report has not finished yet. Open the term again when it has completed.',
      );
      return;
    }
    if (this.saved()) {
      this.saved.set(null);
      this.appliedId = null;
    }
    this.termView.set({ report, term });
    this.queryBar().load(term.expression);
    this.grid().resetFilters();
    this.search.set(this.termSearch(report, term));
  }

  private termSearch(report: SearchTermReport, term: ReportTerm): GridSearch {
    return {
      query: term.expression,
      termReport: { reportId: report.reportId, termId: term.termId },
    };
  }

  private dropTerm(message: string): void {
    this.appliedTerm = null;
    this.termView.set(null);
    this.toasts.show(message, { tone: 'error' });
    this.setTermInUrl(true);
    if (this.search().deferred) this.search.set({ query: '' });
  }

  protected clearTerm(): void {
    this.forgetTerm();
    this.queryBar().clear();
    this.search.set({ query: '' });
  }

  /** The list no longer shows the report term (another query ran, or it was cleared). */
  private forgetTerm(): void {
    this.termView.set(null);
    this.appliedTerm = null;
    this.setTermInUrl(true);
  }

  private setTermInUrl(replaceUrl: boolean): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [TERM_REPORT_PARAM]: null, [TERM_PARAM]: null },
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  // ── Saved searches ──────────────────────────────────────────────────────────────────────────────────────────

  /** Runs a saved search from the browser pane: a history entry, like following a link to it. */
  protected runSaved(summary: SavedSearchSummary): void {
    if (summary.savedSearchId === this.appliedId) {
      const saved = this.saved();
      if (saved) this.search.set(this.searchFor(saved.query, saved.savedSearchId));
      return;
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        [SAVED_SEARCH_PARAM]: summary.savedSearchId,
        [THEN_PARAM]: null,
        [TERM_REPORT_PARAM]: null,
        [TERM_PARAM]: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  /** Applies `?savedSearch=`: loads it, shows its query and runs it by id (filtered for the caller by the API). */
  private async applySaved(id: string): Promise<void> {
    this.appliedId = id;
    let saved: SavedSearch;
    try {
      saved = await this.savedApi.get(id);
    } catch (e) {
      if (this.appliedId !== id) return;
      this.appliedId = null;
      this.pendingMassEdit = null;
      this.toasts.show(savedSearchErrorText(toApiError(e)), { tone: 'error' });
      this.setSavedInUrl(null, true);
      if (this.search().deferred) this.search.set(this.searchFor(''));
      return;
    }
    if (this.appliedId !== id) return;
    this.showSaved(saved);
  }

  /**
   * The list shows `saved`: its name in the search panel, its query in the bar, run by id. Its columns and sort (saved
   * with it from Documents, E16-T09) are applied to the list; a search saved without them keeps the current View.
   */
  private showSaved(saved: SavedSearch, applyView = true): void {
    this.termView.set(null);
    this.appliedTerm = null;
    this.appliedId = saved.savedSearchId;
    this.saved.set(saved);
    this.queryBar().load(saved.query);
    this.grid().resetFilters();
    this.include.set(includeOf(saved));
    if (applyView && (saved.columns.length > 0 || saved.sort.length > 0)) {
      this.grid().applyColumns(
        saved.columns.length > 0 ? saved.columns.map((field) => ({ field })) : null,
        saved.sort.length > 0 ? saved.sort : null,
        false,
      );
    }
    this.search.set(this.searchFor(saved.query, saved.savedSearchId));
  }

  protected clearSaved(): void {
    this.forgetSaved();
    this.queryBar().clear();
    this.search.set(this.searchFor(''));
  }

  /** The list no longer shows the saved search (another query ran, or it was cleared). */
  private forgetSaved(): void {
    this.saved.set(null);
    this.appliedId = null;
    this.pendingMassEdit = null;
    this.setSavedInUrl(null, true);
  }

  private setSavedInUrl(id: string | null, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [SAVED_SEARCH_PARAM]: id, [THEN_PARAM]: null },
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  /** "Save current search": the list's query (keyword and filters), columns and sort as a new saved search. */
  protected async saveCurrent(): Promise<void> {
    const query = this.grid().effectiveQuery();
    const columns = this.grid().savedSearchColumns();
    const sort = this.grid().sortKeys();
    const folders = await this.savedApi.folders().catch(() => []);
    const { SavedSearchDialog } = await import('../searches/saved-search-dialog');
    const ref = this.dialogs.open<SavedSearch, SavedSearchDialogData>(SavedSearchDialog, {
      data: { mode: 'create', folders, query, include: this.include(), columns, sort },
      width: '44rem',
      autoFocus: 'input',
      injector: this.injector,
    });
    const result = await firstValueFrom(ref.closed);
    if (!result) return;
    this.toasts.show(`Saved search “${result.name}” created.`, { tone: 'success' });
    void this.browser()?.reload();
    // Same documents and columns, run by id from now on, so its last run and hit count are recorded.
    this.showSaved(result, false);
    this.setSavedInUrl(result.savedSearchId, true);
  }

  protected onLoaded(search: GridSearch): void {
    const id = this.pendingMassEdit;
    if (!id || search.savedSearchId !== id) return;
    this.pendingMassEdit = null;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [THEN_PARAM]: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
    this.grid().selectAllResults();
    if (!this.grid().selectionTarget()) {
      this.toasts.show('The saved search found no documents to edit.', { tone: 'info' });
      return;
    }
    // Mass Actions reads the selection through its input: open the dialog once it is bound.
    afterNextRender(() => void this.massActions()?.massEdit(), { injector: this.injector });
  }

  protected toggleBrowser(): void {
    const open = !this.browserOpen();
    this.browserOpen.set(open);
    this.storage.write(BROWSER_KEY, { open });
  }

  protected onOpen(event: GridOpenEvent): void {
    this.cursor.open(event.hit.documentId);
    this.reviewing.set(true);
    if (!this.reviewInUrl()) this.setReviewInUrl(true);
  }

  /** "Back to list" (unsaved edits already saved or discarded): drops the Review mode history entry too. */
  protected closeReview(): void {
    this.reviewing.set(false);
    this.grid().restoreView();
    if (this.reviewInUrl()) this.location.back();
  }

  /** The browser left the Review mode entry: close it, or put the entry back if the reviewer cancels. */
  private async onBrowserBack(): Promise<void> {
    const review = this.review();
    if (review && !(await review.canLeave())) {
      this.setReviewInUrl(true);
      return;
    }
    this.reviewing.set(false);
    this.grid().restoreView();
  }

  /**
   * The URL says Review mode but the page is in List mode: Forward after leaving it reopens the last reviewed
   * document; a reload or a pasted link (nothing to review yet) just shows the list.
   */
  private onReviewEntry(): void {
    if (this.cursor.documentId()) this.reviewing.set(true);
    else this.setReviewInUrl(false, true);
  }

  private setReviewInUrl(on: boolean, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [REVIEW_PARAM]: on ? REVIEW_VALUE : null },
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  /**
   * The duplicate indicator, or "Show … in the list" in Related Items (E16-T10): the relation becomes the search of the
   * list (an ordinary query, so it can be refined, saved or used for Mass Edit). Review mode closes first.
   */
  protected onPivot(pivot: RelationshipPivot): void {
    if (this.reviewing()) this.closeReview();
    if (this.saved()) this.forgetSaved();
    if (this.termView()) this.forgetTerm();
    const query = pivotQuery(pivot);
    this.grid().resetFilters();
    this.queryBar().load(query);
    this.search.set(this.searchFor(query));
    this.toasts.show(`Showing: ${pivotLabel(pivot)}.`, { tone: 'info' });
  }

  protected onRefreshed(message: string): void {
    if (this.reviewing()) this.review()?.notify(message);
  }
}
