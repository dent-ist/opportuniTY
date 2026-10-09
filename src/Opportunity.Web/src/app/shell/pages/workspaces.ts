import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { INSTALLATION_PERMISSIONS } from '../../core/workspace/sections';
import {
  Workspace,
  WorkspaceDirectory,
  WorkspaceSummary,
} from '../../core/workspace/workspace-api';
import { RecentWorkspaces } from '../../core/workspace/workspace-context';
import { NewWorkspaceDialog } from '../../features/workspace-admin/new-workspace-dialog';
import {
  Badge,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  TextField,
  ToastService,
} from '../../ui';

/**
 * Installation-level Workspaces list, the first page after sign-in (familiarity guide §2.1): recent
 * workspaces, then every workspace the user is a member of, with a filter by name or matter number.
 * Installation admins create workspaces here (E04-T07); a new workspace opens on its setup checklist.
 * `?new=true` opens New workspace (the return address of the MFA sign-in it may ask for).
 */
@Component({
  selector: 'opp-workspaces-page',
  imports: [RouterLink, Badge, Button, EmptyState, ErrorState, Icon, LoadingState, TextField],
  templateUrl: './workspaces.html',
  styleUrl: './workspaces.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspacesPage {
  private readonly directory = inject(WorkspaceDirectory);
  private readonly recentIds = inject(RecentWorkspaces).ids;
  private readonly locale = inject(UiPreferences).locale;
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Display only: the API authorizes the create call (Installation.ManageWorkspaces + MFA). */
  private readonly session = inject(SessionService);
  protected readonly canCreate = computed(() =>
    this.session.hasInstallationPermission(INSTALLATION_PERMISSIONS.manageWorkspaces),
  );
  protected readonly canApproveDeletions = computed(() =>
    this.session.hasInstallationPermission(INSTALLATION_PERMISSIONS.approveDeletion),
  );

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

  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.locale(), { dateStyle: 'medium' }),
  );

  constructor() {
    void this.load();
    if (this.route.snapshot.queryParamMap.get('new') === 'true') {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { new: null },
        replaceUrl: true,
      });
      if (this.canCreate()) void this.create();
    }
  }

  protected date(value: unknown): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime()) ? '' : this.dates().format(d);
  }

  /** New workspace; on success the new workspace opens on its setup checklist. */
  protected async create(): Promise<void> {
    const ref = this.dialogs.open<Workspace>(NewWorkspaceDialog, {
      width: '36rem',
      autoFocus: 'input, button[oppButton="primary"]',
    });
    const workspace = await firstValueFrom(ref.closed);
    if (!workspace) return;
    this.toasts.show(`Workspace ${workspace.name} created`, { tone: 'success' });
    await this.router.navigate(['/w', workspace.workspaceId, 'admin', 'setup']);
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
