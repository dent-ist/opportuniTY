import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { EmptyState } from '../../ui';
import { QueryBar, QuerySubmission } from './search/query-bar';

/**
 * Documents: the default landing page of a workspace (familiarity guide §2.1, §3). The search panel holds
 * the keyword query bar (E16-T01); the conditions builder, the document list (E16-T02) and review mode
 * follow with E16. Until the search endpoint exists a valid search is acknowledged, not run.
 *
 * Keyboard: Alt+Shift+K (⌥⇧K) focuses the keyword search (guide §4).
 */
@Component({
  selector: 'opp-documents-page',
  imports: [EmptyState, QueryBar],
  template: `<h1 class="documents__title">Documents</h1>
    <section class="documents__search" aria-labelledby="documents-search-heading">
      <h2 id="documents-search-heading" class="opp-visually-hidden">Search</h2>
      <opp-query-bar (search)="lastSearch.set($event)" />
    </section>
    <section aria-label="Document list">
      @if (lastSearch(); as search) {
        <opp-empty-state
          title="Document list not available yet"
          [message]="
            search.query.trim()
              ? 'The query is valid. Results will appear here once the document list is available.'
              : 'Browsing all documents will be possible once the document list is available.'
          "
        />
      } @else {
        <opp-empty-state
          title="No document list yet"
          [message]="
            'Search, the document list and review mode for ' +
            context.workspace.name +
            ' will appear here.'
          "
        />
      }
    </section>`,
  styleUrl: './documents-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'onShortcut($event)' },
})
export class DocumentsPage {
  protected readonly context = inject(WorkspaceContext);
  protected readonly lastSearch = signal<QuerySubmission | null>(null);
  private readonly queryBar = viewChild.required(QueryBar);

  protected onShortcut(event: KeyboardEvent): void {
    if (
      event.altKey &&
      event.shiftKey &&
      !event.ctrlKey &&
      !event.metaKey &&
      event.code === 'KeyK'
    ) {
      event.preventDefault();
      this.queryBar().focus();
    }
  }
}
