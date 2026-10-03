import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { EmptyState } from '../../ui';

/**
 * Documents: the default landing page of a workspace (familiarity guide §2.1, §3). The browser pane,
 * search panel, document list and review mode land here with E16; until then it is an empty state.
 */
@Component({
  selector: 'opp-documents-page',
  imports: [EmptyState],
  template: `<h1 class="documents__title">Documents</h1>
    <opp-empty-state
      title="No document list yet"
      [message]="
        'Search, the document list and review mode for ' +
        context.workspace.name +
        ' will appear here.'
      "
    />`,
  styleUrl: './documents-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentsPage {
  protected readonly context = inject(WorkspaceContext);
}
