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
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { Icon, Tree, TreeNode } from '../../../ui';
import { DocumentFolder, FolderApi } from './folder-api';
import { DisplayFormat } from '../../../core/i18n';

/** Tree ids of the helper items under a folder (or the top level, ``). */
const MORE_PREFIX = 'more:';
const LOADING_PREFIX = 'loading:';
const RETRY_PREFIX = 'retry:';
const TOP = '';

interface LevelState {
  readonly items: readonly DocumentFolder[];
  readonly nextCursor: string | null;
  readonly loading: boolean;
  readonly failed: boolean;
}

/**
 * Folders in the Documents browser pane (#179, Q-55): the folder tree the imports' Folder Path built, read-only. It
 * loads one level at a time (a folder's subfolders when it is first opened, "Show more folders" for long levels), so
 * it stays fast in very large workspaces. Each folder shows how many documents the caller may see in it (with or
 * without subfolders, following the list's choice); folders holding none are not listed. Selecting a folder (Enter or
 * a click) lists its documents.
 */
@Component({
  selector: 'opp-folder-browser',
  imports: [Icon, Tree],
  template: `<div class="fb__header">
      <h2 [id]="headingId()" class="fb__title">Folders</h2>
    </div>
    @if (error(); as e) {
      <p class="fb__note" role="alert">
        <opp-icon name="error" />
        {{ e.status === 404 ? 'Folders are not available.' : 'Folders could not be loaded.' }}
        <button type="button" class="fb__link" (click)="reload()">Reload folders</button>
      </p>
    } @else if (loaded() && nodes().length === 0) {
      <p class="fb__note">No folders. They come from the Folder Path an import maps.</p>
    } @else if (loaded()) {
      <opp-tree
        label="Folders"
        [nodes]="nodes()"
        [selected]="activeId()"
        (activate)="onActivate($event)"
        (expandedChange)="onExpanded($event)"
      />
      @if (approximate()) {
        <p class="fb__note">≈ Counts are catching up with recent changes.</p>
      }
    } @else {
      <p class="fb__note" role="status">Loading folders…</p>
    }`,
  styleUrl: './folder-browser.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FolderBrowser {
  /** The folder the list shows, if any. */
  readonly activeId = input<string | null>(null);
  /** Whether counts include subfolders (the list's "Include subfolders" choice). */
  readonly includeSubfolders = input(true);
  /** Id of the heading, which labels the section. */
  readonly headingId = input('folder-browser-heading');
  readonly selectFolder = output<DocumentFolder>();

  private readonly api = inject(FolderApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly tree = viewChild(Tree);

  /** Loaded levels by parent folder id (`` for the top level). */
  private readonly levels = signal<ReadonlyMap<string, LevelState>>(new Map());
  protected readonly error = signal<ApiError | null>(null);
  protected readonly approximate = signal(false);
  protected readonly loaded = computed(() => {
    const top = this.levels().get(TOP);
    return !!top && !(top.loading && top.items.length === 0);
  });

  private readonly byId = computed(() => {
    const map = new Map<string, DocumentFolder>();
    for (const level of this.levels().values()) for (const f of level.items) map.set(f.folderId, f);
    return map;
  });

  protected readonly nodes = computed<TreeNode[]>(() => this.children(TOP));

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.error.set(null);
    this.levels.set(new Map());
    await this.load(TOP, null);
  }

  focus(): void {
    this.tree()?.focus();
  }

  protected onExpanded(event: { id: string; expanded: boolean }): void {
    if (event.expanded && this.byId().has(event.id) && !this.levels().has(event.id)) {
      void this.load(event.id, null);
    }
  }

  protected onActivate(node: TreeNode): void {
    if (node.id.startsWith(MORE_PREFIX)) {
      const parent = node.id.slice(MORE_PREFIX.length);
      void this.load(parent, this.levels().get(parent)?.nextCursor ?? null);
      return;
    }
    if (node.id.startsWith(RETRY_PREFIX)) {
      void this.load(node.id.slice(RETRY_PREFIX.length), null);
      return;
    }
    if (node.id.startsWith(LOADING_PREFIX)) return;
    const folder = this.byId().get(node.id);
    if (folder) this.selectFolder.emit(folder);
  }

  private children(parent: string): TreeNode[] {
    const level = this.levels().get(parent);
    if (!level || (level.loading && level.items.length === 0)) {
      return parent === TOP ? [] : [{ id: LOADING_PREFIX + parent, label: 'Loading folders…' }];
    }
    const nodes: TreeNode[] = level.items.map((f) => this.node(f));
    if (level.failed) {
      nodes.push({
        id: RETRY_PREFIX + parent,
        label: 'Could not load folders. Try again',
        icon: 'refresh',
      });
    } else if (level.loading) {
      nodes.push({ id: LOADING_PREFIX + parent, label: 'Loading folders…' });
    } else if (level.nextCursor) {
      nodes.push({ id: MORE_PREFIX + parent, label: 'Show more folders', icon: 'chevron-down' });
    }
    return nodes;
  }

  private node(f: DocumentFolder): TreeNode {
    const count = this.includeSubfolders() ? f.totalCount : f.documentCount;
    const detail = `${this.approximate() ? '≈' : ''}${this.fmt.number(count)} ${count === 1 ? 'document' : 'documents'}`;
    return {
      id: f.folderId,
      label: f.name,
      icon: 'folder',
      detail,
      ...(f.hasSubfolders ? { children: this.children(f.folderId) } : {}),
    };
  }

  private async load(parent: string, cursor: string | null): Promise<void> {
    const previous = this.levels().get(parent);
    const items = cursor ? (previous?.items ?? []) : [];
    this.setLevel(parent, { items, nextCursor: null, loading: true, failed: false });
    try {
      const level = await this.api.level(parent === TOP ? null : parent, cursor);
      if (level.countsApproximate) this.approximate.set(true);
      this.setLevel(parent, {
        items: [...items, ...level.items],
        nextCursor: level.nextCursor,
        loading: false,
        failed: false,
      });
    } catch (e) {
      if (parent === TOP && items.length === 0) {
        this.error.set(toApiError(e));
        return;
      }
      this.setLevel(parent, { items, nextCursor: cursor, loading: false, failed: true });
    }
  }

  private setLevel(parent: string, state: LevelState): void {
    this.levels.update((levels) => new Map(levels).set(parent, state));
  }
}
