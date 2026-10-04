import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Button, Icon, Tree, TreeNode } from '../../../ui';
import {
  SavedSearchApi,
  SavedSearchFolder,
  SavedSearchSummary,
} from '../../searches/saved-search-api';
import { FolderNode, folderTree } from '../../searches/saved-search-model';

const FOLDER_PREFIX = 'folder:';

/**
 * Saved Searches in the Documents browser pane (familiarity guide §3.1, E16-T11): a compact tree of the folders and
 * saved searches the caller may see, for running one with Enter or a click, plus "Save current search". Managing them
 * (edit, move, share, delete) happens in Searches › Saved Searches, linked from here.
 */
@Component({
  selector: 'opp-saved-search-browser',
  imports: [Button, Icon, RouterLink, Tree],
  template: `<div class="ssb__header">
      <h2 [id]="headingId()" class="ssb__title">Saved Searches</h2>
      <a
        class="ssb__manage"
        [routerLink]="['/w', workspaceId, 'searches', 'saved']"
        aria-label="Manage saved searches"
        >Manage</a
      >
    </div>
    <button type="button" oppButton="secondary" class="ssb__save" (click)="save.emit()">
      <opp-icon name="save" />
      Save current search
    </button>
    @if (error(); as e) {
      <p class="ssb__note" role="alert">
        <opp-icon name="error" />
        {{
          e.status === 404
            ? 'Saved searches are not available.'
            : 'Saved searches could not be loaded.'
        }}
        <button type="button" class="ssb__link" (click)="reload()">Reload saved searches</button>
      </p>
    } @else if (loaded() && nodes().length === 0) {
      <p class="ssb__note">No saved searches yet. Save the current search to keep it.</p>
    } @else if (loaded()) {
      <opp-tree
        label="Saved searches"
        [nodes]="nodes()"
        [selected]="activeId()"
        [initiallyExpanded]="expanded()"
        (activate)="onActivate($event)"
      />
    } @else {
      <p class="ssb__note" role="status">Loading saved searches…</p>
    }`,
  styleUrl: './saved-search-browser.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SavedSearchBrowser {
  /** The saved search the list shows, if any. */
  readonly activeId = input<string | null>(null);
  /** Id of the heading, which labels the landmark the page puts around the pane. */
  readonly headingId = input('saved-search-browser-heading');
  readonly run = output<SavedSearchSummary>();
  readonly save = output<void>();

  private readonly api = inject(SavedSearchApi);
  protected readonly workspaceId = inject(WorkspaceContext).workspaceId;
  private readonly tree = viewChild(Tree);

  private readonly folders = signal<readonly SavedSearchFolder[]>([]);
  private readonly searches = signal<readonly SavedSearchSummary[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly nodes = computed<TreeNode[]>(() => {
    const byFolder = new Map<string | null, SavedSearchSummary[]>();
    const known = new Set(this.folders().map((f) => f.folderId));
    for (const s of this.searches()) {
      const folder = s.folderId && known.has(s.folderId) ? s.folderId : null;
      byFolder.set(folder, [...(byFolder.get(folder) ?? []), s]);
    }
    const leaf = (s: SavedSearchSummary): TreeNode => ({
      id: s.savedSearchId,
      label: s.name,
      icon: 'search',
      ...(s.scope === 'shared' ? { detail: 'Shared' } : {}),
    });
    const sorted = (list: SavedSearchSummary[] = []) =>
      [...list].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
    const folder = (n: FolderNode): TreeNode => ({
      id: FOLDER_PREFIX + n.folder.folderId,
      label: n.folder.name,
      icon: 'folder',
      children: [...n.children.map(folder), ...sorted(byFolder.get(n.folder.folderId)).map(leaf)],
    });
    return [...folderTree(this.folders()).map(folder), ...sorted(byFolder.get(null)).map(leaf)];
  });
  /** Top-level folders start open, so the searches are one arrow key away. */
  protected readonly expanded = computed(() =>
    folderTree(this.folders()).map((n) => FOLDER_PREFIX + n.folder.folderId),
  );

  private readonly byId = computed(
    () => new Map(this.searches().map((s) => [s.savedSearchId, s] as const)),
  );

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.error.set(null);
    try {
      const [folders, searches] = await Promise.all([this.api.folders(), this.api.listAll()]);
      this.folders.set(folders);
      this.searches.set(searches);
      this.loaded.set(true);
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  focus(): void {
    this.tree()?.focus();
  }

  protected onActivate(node: TreeNode): void {
    if (node.id.startsWith(FOLDER_PREFIX)) {
      this.tree()?.toggle(node.id);
      return;
    }
    const saved = this.byId().get(node.id);
    if (saved) this.run.emit(saved);
  }
}
