import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  MENU,
  TextField,
  ToastService,
  Tree,
  TreeNode,
} from '../../ui';
import { FolderDialog, FolderDialogData } from './folder-dialog';
import {
  FrozenSetSummary,
  HttpSavedSearchApi,
  SavedSearch,
  SavedSearchApi,
  SavedSearchFolder,
  SavedSearchSummary,
} from './saved-search-api';
import {
  SavedSearchDialog,
  SavedSearchDialogData,
  SavedSearchDialogMode,
} from './saved-search-dialog';
import {
  FolderNode,
  LIVE_EXPLANATION,
  LIVE_LABEL,
  SAVED_SEARCH_PARAM,
  THEN_PARAM,
  actionsFor,
  folderPath,
  folderTree,
  formatDate,
  freshnessLabel,
  frozenLabel,
  hitCountLabel,
  savedSearchErrorText,
  sharingLabel,
} from './saved-search-model';
import { ShareDialog, ShareDialogData } from './share-dialog';

const ALL = 'all';
const UNFILED = 'unfiled';
const FROZEN = 'frozen';

/** A row of the list with what the caller may do with it. */
interface Row {
  readonly s: SavedSearchSummary;
  readonly folder: string;
  readonly owner: string;
  readonly sharing: string;
  readonly mine: boolean;
  readonly lastRun: string;
  readonly hits: string;
  readonly freshness: string;
  readonly modified: string;
  readonly actions: ReturnType<typeof actionsFor>;
}

/**
 * Searches › Saved Searches (E16-T11, familiarity guide §2.3, Q-65): the folder tree, and the saved searches the
 * caller may see (their own, those shared with them or their groups; Workspace Admins see all) with folder,
 * owner, sharing, last run, last hit count with its freshness and modified date. Create, edit, rename, move, copy,
 * delete, share (`SavedSearch.Share`), run in Documents (`?savedSearch=`) and Mass Edit the results. Frozen sets
 * (snapshots) are a separate node: they keep the documents they had when frozen, while saved searches are live.
 */
@Component({
  selector: 'opp-saved-searches-page',
  imports: [
    Badge,
    Button,
    EmptyState,
    ErrorState,
    Icon,
    IconButton,
    LoadingState,
    RouterLink,
    TextField,
    Tree,
    ...MENU,
  ],
  templateUrl: './saved-searches-page.html',
  styleUrl: './saved-searches-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: SavedSearchApi, useClass: HttpSavedSearchApi }],
})
export class SavedSearchesPage {
  private readonly api = inject(SavedSearchApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly me = inject(SessionService).principal;
  private readonly tree = viewChild(Tree);

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly liveLabel = LIVE_LABEL;
  protected readonly liveExplanation = LIVE_EXPLANATION;
  protected readonly canCreate = this.context.can(PERMISSIONS.searchExecute);

  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly folders = signal<readonly SavedSearchFolder[]>([]);
  protected readonly searches = signal<readonly SavedSearchSummary[]>([]);
  protected readonly frozen = signal<readonly FrozenSetSummary[] | null>(null);
  protected readonly frozenError = signal<ApiError | null>(null);
  protected readonly node = signal<string>(ALL);
  protected readonly filter = signal('');

  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';
  private readonly caller = computed(() => ({
    userId: this.me()?.userId ?? null,
    can: (p: string) => this.context.can(p),
  }));

  protected readonly treeNodes = computed<TreeNode[]>(() => {
    const counts = new Map<string | null, number>();
    for (const s of this.searches()) counts.set(s.folderId, (counts.get(s.folderId) ?? 0) + 1);
    const toNode = (n: FolderNode): TreeNode => ({
      id: n.folder.folderId,
      label: n.folder.name,
      icon: 'folder',
      detail: String(counts.get(n.folder.folderId) ?? 0),
      ...(n.children.length ? { children: n.children.map(toNode) } : {}),
    });
    return [
      {
        id: ALL,
        label: 'All saved searches',
        icon: 'search',
        detail: String(this.searches().length),
      },
      ...folderTree(this.folders()).map(toNode),
      {
        id: UNFILED,
        label: 'Not in a folder',
        icon: 'documents',
        detail: String(counts.get(null) ?? 0),
      },
      { id: FROZEN, label: 'Frozen sets', icon: 'snapshot' },
    ];
  });

  protected readonly selectedFolder = computed(
    () => this.folders().find((f) => f.folderId === this.node()) ?? null,
  );
  protected readonly showingFrozen = computed(() => this.node() === FROZEN);
  protected readonly listTitle = computed(() => {
    const node = this.node();
    if (node === ALL) return 'All saved searches';
    if (node === UNFILED) return 'Saved searches not in a folder';
    if (node === FROZEN) return 'Frozen sets';
    return folderPath(node, this.folders());
  });

  protected readonly rows = computed<Row[]>(() => {
    const node = this.node();
    const q = this.filter().trim().toLowerCase();
    const locale = this.prefs.locale();
    const caller = this.caller();
    const folders = this.folders();
    return this.searches()
      .filter((s) =>
        node === ALL ? true : node === UNFILED ? s.folderId === null : s.folderId === node,
      )
      .filter((s) => !q || s.name.toLowerCase().includes(q))
      .map((s) => {
        const mine = s.owner.userId === caller.userId;
        return {
          s,
          folder: folderPath(s.folderId, folders),
          owner: mine ? 'You' : s.owner.displayName,
          sharing: !mine && s.scope === 'shared' ? 'Shared with you' : sharingLabel(s),
          mine,
          lastRun: s.lastRunAt ? formatDate(s.lastRunAt, locale, this.timeZone) : 'Never',
          hits: hitCountLabel(s, locale),
          freshness: freshnessLabel(s, locale, this.timeZone),
          modified: formatDate(s.modifiedAt, locale, this.timeZone),
          actions: actionsFor(s, caller),
        };
      });
  });

  protected readonly frozenRows = computed(() => {
    const locale = this.prefs.locale();
    const me = this.me()?.userId ?? null;
    return (this.frozen() ?? []).map((f) => ({
      f,
      label: frozenLabel(f, me, locale, this.timeZone),
      count:
        f.documentCount === null
          ? 'Freezing…'
          : new Intl.NumberFormat(locale).format(f.documentCount),
      at: formatDate(f.frozenAt, locale, this.timeZone),
      by: f.createdBy === me ? 'You' : 'Another user',
      purpose: PURPOSES[f.purpose] ?? f.purpose,
    }));
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [folders, searches] = await Promise.all([this.api.folders(), this.api.listAll()]);
      this.folders.set(folders);
      this.searches.set(searches);
      const node = this.node();
      if (
        node !== ALL &&
        node !== UNFILED &&
        node !== FROZEN &&
        !folders.some((f) => f.folderId === node)
      )
        this.node.set(ALL);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected selectNode(node: TreeNode): void {
    this.node.set(node.id);
    if (node.id === FROZEN && this.frozen() === null) void this.loadFrozen();
  }

  protected async loadFrozen(): Promise<void> {
    this.frozenError.set(null);
    try {
      this.frozen.set(await this.api.frozenSets());
    } catch (e) {
      this.frozenError.set(toApiError(e));
    }
  }

  protected runParams(s: SavedSearchSummary): Record<string, string> {
    return { [SAVED_SEARCH_PARAM]: s.savedSearchId };
  }

  protected run(s: SavedSearchSummary, then?: 'massEdit'): void {
    void this.router.navigate(['/w', this.workspaceId, 'documents'], {
      queryParams: {
        [SAVED_SEARCH_PARAM]: s.savedSearchId,
        ...(then ? { [THEN_PARAM]: then } : {}),
      },
    });
  }

  protected notAvailable(what: string): void {
    this.toasts.show(`${what} is not available in this version.`, { tone: 'info' });
  }

  // ── Saved searches ──────────────────────────────────────────────────────────────────────────────────────────

  protected async create(): Promise<void> {
    const folder = this.selectedFolder();
    await this.openEditor('create', undefined, folder?.folderId ?? null);
  }

  protected async open(mode: Exclude<SavedSearchDialogMode, 'create'>, s: SavedSearchSummary) {
    let saved: SavedSearch;
    try {
      saved = await this.api.get(s.savedSearchId);
    } catch (e) {
      this.fail(e);
      return;
    }
    await this.openEditor(mode, saved);
  }

  private async openEditor(
    mode: SavedSearchDialogMode,
    saved?: SavedSearch,
    folderId: string | null = null,
  ): Promise<void> {
    const ref = this.dialogs.open<SavedSearch, SavedSearchDialogData>(SavedSearchDialog, {
      data: { mode, saved, folders: this.folders(), folderId },
      width: mode === 'create' || mode === 'edit' ? '44rem' : '30rem',
      autoFocus: mode === 'move' ? 'select' : 'input',
      injector: this.injector,
    });
    const result = await firstValueFrom(ref.closed);
    if (!result) return;
    const done: Record<SavedSearchDialogMode, string> = {
      create: `Saved search “${result.name}” created.`,
      edit: `Saved search “${result.name}” saved.`,
      rename: `Renamed to “${result.name}”.`,
      move: `“${result.name}” moved to ${folderPath(result.folderId, this.folders())}.`,
      copy: `Copy “${result.name}” created.`,
    };
    this.toasts.show(done[mode], { tone: 'success' });
    await this.load();
  }

  protected async share(s: SavedSearchSummary): Promise<void> {
    let saved: SavedSearch;
    try {
      saved = await this.api.get(s.savedSearchId);
    } catch (e) {
      this.fail(e);
      return;
    }
    const ref = this.dialogs.open<SavedSearch, ShareDialogData>(ShareDialog, {
      data: { saved },
      width: '34rem',
      injector: this.injector,
    });
    const result = await firstValueFrom(ref.closed);
    if (!result) return;
    this.toasts.show(
      result.scope === 'shared'
        ? `“${result.name}” is ${sharingLabel(result).toLowerCase()}.`
        : `“${result.name}” is private.`,
      { tone: 'success' },
    );
    await this.load();
  }

  protected async remove(s: SavedSearchSummary): Promise<void> {
    const shared = s.scope === 'shared' ? ' People it is shared with will no longer see it.' : '';
    const ok = await this.dialogs.confirm({
      title: 'Delete saved search?',
      message: `“${s.name}” will be deleted.${shared} Documents and their coding are not changed.`,
      confirmLabel: 'Delete saved search',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await this.api.delete(s.savedSearchId, s.version);
      this.toasts.show(`Saved search “${s.name}” deleted.`, { tone: 'success' });
    } catch (e) {
      this.fail(e);
    }
    await this.load();
  }

  // ── Folders ─────────────────────────────────────────────────────────────────────────────────────────────────

  protected async newFolder(): Promise<void> {
    await this.openFolder({
      folders: this.folders(),
      parentFolderId: this.selectedFolder()?.folderId ?? null,
    });
  }

  protected async editFolder(folder: SavedSearchFolder): Promise<void> {
    await this.openFolder({ folders: this.folders(), folder });
  }

  private async openFolder(data: FolderDialogData): Promise<void> {
    const ref = this.dialogs.open<SavedSearchFolder, FolderDialogData>(FolderDialog, {
      data,
      width: '28rem',
      autoFocus: 'input',
      injector: this.injector,
    });
    const result = await firstValueFrom(ref.closed);
    if (!result) return;
    this.toasts.show(
      data.folder ? `Folder “${result.name}” saved.` : `Folder “${result.name}” created.`,
      { tone: 'success' },
    );
    await this.load();
    this.node.set(result.folderId);
    queueMicrotask(() => this.tree()?.focus());
  }

  protected async deleteFolder(folder: SavedSearchFolder): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: 'Delete folder?',
      message: `The folder “${folder.name}” will be deleted. Only an empty folder can be deleted.`,
      confirmLabel: 'Delete folder',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await this.api.deleteFolder(folder);
      this.toasts.show(`Folder “${folder.name}” deleted.`, { tone: 'success' });
      this.node.set(ALL);
    } catch (e) {
      this.fail(e);
    }
    await this.load();
  }

  private fail(e: unknown): void {
    this.toasts.show(savedSearchErrorText(toApiError(e)), { tone: 'error' });
  }
}

const PURPOSES: Record<string, string> = {
  bulkCoding: 'Mass Edit',
  export: 'Export',
  production: 'Production',
  report: 'Report',
};
