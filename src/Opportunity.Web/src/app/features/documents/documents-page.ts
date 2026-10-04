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
import { CommandRegionDirective, CommandRegistry } from '../../core/commands';
import { GridOpenEvent, GridSearch, ReviewGrid } from './grid/review-grid';
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
 * Keyboard (guide §4, command registry E15-T03): "Focus keyword search" (Alt+Shift+K, or `/` while single-key
 * shortcuts are on) and the region cycle (Alt+Shift+G / Alt+Shift+B) between the search panel and the list, or
 * between the review panes.
 */
@Component({
  selector: 'opp-documents-page',
  imports: [CommandRegionDirective, QueryBar, ReviewGrid, ReviewWorkspace],
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
        />
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

  constructor() {
    inject(CommandRegistry).handle('search.focus', () => this.queryBar().focus(), {
      enabled: () => !this.reviewing(),
    });
    // The list follows the cursor while it is hidden, so it comes back on the reviewed document.
    effect(() => {
      const id = this.cursor.documentId();
      if (id && this.reviewing()) untracked(() => this.grid().focusDocument(id));
    });
  }

  protected onSearch(submission: QuerySubmission): void {
    this.search.set({ query: submission.query });
  }

  protected onOpen(event: GridOpenEvent): void {
    this.cursor.open(event.hit.documentId);
    this.reviewing.set(true);
  }

  protected closeReview(): void {
    this.reviewing.set(false);
    this.grid().restoreView();
  }

  protected onRefreshed(message: string): void {
    if (this.reviewing()) this.review()?.notify(message);
  }
}
