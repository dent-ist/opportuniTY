import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { CommandRegionDirective, CommandRegistry } from '../../core/commands';
import { ToastService } from '../../ui';
import { GridOpenEvent, GridSearch, ReviewGrid } from './grid/review-grid';
import { QueryBar, QuerySubmission } from './search/query-bar';

/**
 * Documents: the default landing page of a workspace (familiarity guide §2.1, §3), in List mode: the search
 * panel with the keyword query bar (E16-T01) above the document list (E16-T02). The list opens on every document
 * (an empty query) and shows the results of each search. Review mode follows with E16-T03.
 *
 * Keyboard (guide §4, command registry E15-T03): "Focus keyword search" (Alt+Shift+K, or `/` while single-key
 * shortcuts are on) and the region cycle (Alt+Shift+G / Alt+Shift+B) between the search panel and the list.
 */
@Component({
  selector: 'opp-documents-page',
  imports: [CommandRegionDirective, QueryBar, ReviewGrid],
  template: `<h1 class="documents__title">Documents</h1>
    <section class="documents__search" aria-labelledby="documents-search-heading" oppCommandRegion>
      <h2 id="documents-search-heading" class="opp-visually-hidden">Search</h2>
      <opp-query-bar (search)="onSearch($event)" />
    </section>
    <section aria-labelledby="documents-list-heading" oppCommandRegion>
      <h2 id="documents-list-heading" class="opp-visually-hidden">Document list</h2>
      <opp-review-grid [search]="search()" (open)="onOpen($event)" />
    </section>`,
  styleUrl: './documents-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentsPage {
  protected readonly search = signal<GridSearch>({ query: '' });
  private readonly queryBar = viewChild.required(QueryBar);
  private readonly toasts = inject(ToastService);

  constructor() {
    inject(CommandRegistry).handle('search.focus', () => this.queryBar().focus());
  }

  protected onSearch(submission: QuerySubmission): void {
    this.search.set({ query: submission.query });
  }

  protected onOpen(event: GridOpenEvent): void {
    this.toasts.show(`Review mode is not available yet (${event.hit.controlNumber}).`);
  }
}
