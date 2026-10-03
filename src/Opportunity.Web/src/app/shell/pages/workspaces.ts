import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { WorkspaceDirectory, WorkspaceSummary } from '../../core/workspace/workspace-api';
import { RecentWorkspaces } from '../../core/workspace/workspace-context';
import { Button, EmptyState, ErrorState, LoadingState, TextField } from '../../ui';

/**
 * Installation-level Workspaces list, the first page after sign-in (familiarity guide §2.1): recent
 * workspaces, then every workspace the user is a member of, with a filter by name or matter number.
 */
@Component({
  selector: 'opp-workspaces-page',
  imports: [RouterLink, Button, EmptyState, ErrorState, LoadingState, TextField],
  templateUrl: './workspaces.html',
  styleUrl: './workspaces.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspacesPage {
  private readonly directory = inject(WorkspaceDirectory);
  private readonly recentIds = inject(RecentWorkspaces).ids;
  private readonly locale = inject(UiPreferences).locale;

  protected readonly query = signal('');
  protected readonly items = signal<readonly WorkspaceSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly recent = computed(() => {
    const byId = new Map(this.items().map((ws) => [ws.workspaceId, ws]));
    return this.recentIds()
      .map((id) => byId.get(id))
      .filter((ws) => ws !== undefined);
  });

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLocaleLowerCase(this.locale());
    if (!q) return this.items();
    return this.items().filter((ws) =>
      [ws.name, ws.matterNumber ?? ''].some((text) =>
        text.toLocaleLowerCase(this.locale()).includes(q),
      ),
    );
  });

  protected readonly summary = computed(() => {
    const format = new Intl.NumberFormat(this.locale());
    const shown = this.filtered().length;
    const loaded = this.items().length;
    const more = this.nextCursor() ? ' (more available)' : '';
    if (!this.query().trim()) return `${format.format(loaded)} workspaces${more}`;
    return `${format.format(shown)} of ${format.format(loaded)} workspaces match${more}`;
  });

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.directory.list();
      this.items.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.directory.list(cursor);
      this.items.update((items) => [...items, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }
}
