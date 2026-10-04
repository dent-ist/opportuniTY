import { Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { CommandRegionDirective, CommandRegistry } from '../../core/commands';
import { GridOpenEvent, GridSearch, ReviewGrid } from './grid/review-grid';
import { PendingCoding } from './review/coding/pending-coding';
import { DocumentLoader } from './review/document-loader';
import { CursorSource, ReviewCursor } from './review/review-cursor';
import {
  CodingApi,
  DocumentContentApi,
  HttpCodingApi,
  HttpDocumentContentApi,
} from './review/review-ports';
import { ReviewWorkspace } from './review/review-workspace';
import { QueryBar, QuerySubmission } from './search/query-bar';
import { BulkCodingApi, HttpBulkCodingApi } from './mass-edit/bulk-coding-api';
import { MassActions } from './mass-edit/mass-actions';
import { MassEditJobs } from './mass-edit/mass-edit-jobs';

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
 * Keyboard (guide §4, command registry E15-T03): "Focus keyword search" (Alt+Shift+K, or `/` while single-key
 * shortcuts are on) and the region cycle (Alt+Shift+G / Alt+Shift+B) between the search panel and the list, or
 * between the review panes.
 */
const REVIEW_PARAM = 'view';
const REVIEW_VALUE = 'review';

@Component({
  selector: 'opp-documents-page',
  imports: [CommandRegionDirective, MassActions, QueryBar, ReviewGrid, ReviewWorkspace],
  template: `<h1 class="documents__title" [class.opp-visually-hidden]="reviewing()">Documents</h1>
    <div class="documents__list" [hidden]="reviewing()">
      <section
        class="documents__search"
        aria-labelledby="documents-search-heading"
        oppCommandRegion
      >
        <h2 id="documents-search-heading" class="opp-visually-hidden">Search</h2>
        <opp-query-bar (search)="onSearch($event)" />
      </section>
      <section aria-labelledby="documents-list-heading" oppCommandRegion>
        <h2 id="documents-list-heading" class="opp-visually-hidden">Document list</h2>
        <opp-review-grid
          [search]="search()"
          (open)="onOpen($event)"
          (refreshed)="onRefreshed($event)"
        >
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
    @if (reviewing()) {
      <opp-review-workspace
        [cursor]="cursor"
        [fields]="grid().fieldCatalogue()"
        (back)="closeReview()"
      />
    }`,
  styleUrl: './documents-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    DocumentLoader,
    { provide: DocumentContentApi, useClass: HttpDocumentContentApi },
    { provide: CodingApi, useClass: HttpCodingApi },
    PendingCoding,
    { provide: BulkCodingApi, useClass: HttpBulkCodingApi },
    MassEditJobs,
  ],
  host: { '[class.is-reviewing]': 'reviewing()' },
})
export class DocumentsPage {
  protected readonly search = signal<GridSearch>({ query: '' });
  protected readonly reviewing = signal(false);
  private readonly queryBar = viewChild.required(QueryBar);
  protected readonly grid = viewChild.required(ReviewGrid);
  private readonly review = viewChild(ReviewWorkspace);

  /** The list as the cursor sees it (the grid is a view child, so it is read lazily). */
  private readonly source: CursorSource = {
    rows: computed(() => this.grid().rows()),
    countText: computed(() => this.grid().countText()),
    positionOf: (index) => this.grid().positionOf(index),
    hasMore: (direction) => this.grid().hasMore(direction),
    fetchMore: (direction) => this.grid().fetchMore(direction),
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

  constructor() {
    inject(CommandRegistry).handle('search.focus', () => this.queryBar().focus(), {
      enabled: () => !this.reviewing(),
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
    this.search.set({ query: submission.query });
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

  protected onRefreshed(message: string): void {
    if (this.reviewing()) this.review()?.notify(message);
  }
}
