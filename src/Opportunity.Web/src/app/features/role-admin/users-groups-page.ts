import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiError, describeError, toApiError } from '../../core/api/problem-details';
import {
  HttpRoleAdminApi,
  RoleAdminApi,
  RoleAssignments,
  RoleCatalog,
  RoleChange,
  principalRef,
} from '../../core/security/role-admin-api';
import { WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  TextField,
} from '../../ui';
import { AssignRolesDialog, AssignRolesDialogData } from './assign-roles-dialog';
import {
  APPLIES_IMMEDIATELY,
  BREAK_GLASS,
  CellState,
  Principal,
  WORKSPACE_ADMIN,
  cellState,
  inCatalogueOrder,
  moveInMatrix,
  principalKey,
  roleName,
  youStayAdmin,
} from './role-admin-model';

/**
 * Admin › Users & Groups (E05-T08; familiarity guide §1.1 "Users & Groups", §2.3): who has access to the workspace and
 * through which built-in roles. One row per user or IdP group, one column per role; each cell is a checkbox that
 * saves at once (Space toggles, arrow keys move between cells). The screen follows the API's rules before the server
 * has to refuse: nobody adds a role to themselves, giving up one's own role asks first (with a stronger warning when it
 * ends one's own administration), the last Workspace Admin stays, and Break-glass is for users only and assigned by an
 * Installation Admin. Every change sends the set version as If-Match and is audited by the API.
 */
@Component({
  selector: 'opp-users-groups-page',
  imports: [
    Badge,
    Button,
    Checkbox,
    EmptyState,
    ErrorState,
    Icon,
    IconButton,
    LoadingState,
    TextField,
  ],
  templateUrl: './users-groups-page.html',
  styleUrl: './role-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: RoleAdminApi, useClass: HttpRoleAdminApi }],
  host: { class: 'ra-page' },
})
export class UsersGroupsPage {
  private readonly api = inject(RoleAdminApi);
  private readonly dialogs = inject(DialogService);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly router = inject(Router);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly context = inject(WorkspaceContext);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  protected readonly appliesImmediately = APPLIES_IMMEDIATELY;
  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly catalog = signal<RoleCatalog | null>(null);
  protected readonly assignments = signal<RoleAssignments | null>(null);
  protected readonly filterText = signal('');
  /** Cells being saved (`principalKey|role`), and the optimistic state they show meanwhile. */
  protected readonly saving = signal<ReadonlyMap<string, boolean>>(new Map());
  protected readonly message = signal<string | null>(null);

  protected readonly roles = computed(() => this.catalog()?.roles ?? []);

  protected readonly rows = computed(() => {
    const text = this.filterText().trim().toLowerCase();
    return (this.assignments()?.items ?? []).filter(
      (p) =>
        !text ||
        p.displayName.toLowerCase().includes(text) ||
        (p.email ?? '').toLowerCase().includes(text) ||
        (p.groupName ?? '').toLowerCase().includes(text),
    );
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [catalog, assignments] = await Promise.all([this.api.roles(), this.api.assignments()]);
      this.catalog.set(catalog);
      this.assignments.set(assignments);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected key = principalKey;

  protected cell(p: Principal, role: string): CellState {
    const a = this.assignments();
    const state = cellState(p, role, a ?? { administratorPaths: 0, canAssignBreakGlass: false });
    const pending = this.saving().get(`${principalKey(p)}|${role}`);
    // Shown while saving (or while the self-removal question is open); the control stays focusable for keyboard users.
    return pending === undefined ? state : { ...state, checked: pending };
  }

  protected cellLabel(p: Principal, role: string, state: CellState): string {
    const label = `${roleName(this.catalog(), role)} for ${p.displayName}`;
    return state.reason ? `${label}. ${state.reason}.` : label;
  }

  protected rolesSummary(p: Principal): string {
    return p.roles.length
      ? p.roles.map((r) => roleName(this.catalog(), r)).join(', ')
      : 'No roles: no access to this workspace';
  }

  protected onMatrixKeydown(event: KeyboardEvent): void {
    moveInMatrix(event);
  }

  protected async toggle(
    p: Principal,
    role: string,
    checked: boolean,
    control: Checkbox,
  ): Promise<void> {
    const catalog = this.catalog();
    if (!catalog) return;
    const cellKey = `${principalKey(p)}|${role}`;
    // One save per cell at a time: a second toggle meanwhile is undone.
    if (this.saving().has(cellKey)) {
      control.checked.set(!checked);
      return;
    }
    // The latest state of the row (the control is also re-synced below, which reports the value it already holds).
    p = this.assignments()?.items.find((x) => principalKey(x) === principalKey(p)) ?? p;
    if (checked === p.roles.includes(role)) return;
    const roles = checked
      ? inCatalogueOrder(catalog, [...p.roles, role])
      : p.roles.filter((r) => r !== role);
    this.saving.update((m) => new Map(m).set(cellKey, checked));
    try {
      const name = roleName(catalog, role);
      await this.replace(
        p,
        roles,
        checked ? `${name} assigned to ${p.displayName}` : `${name} removed from ${p.displayName}`,
      );
    } finally {
      this.saving.update((m) => {
        const next = new Map(m);
        next.delete(cellKey);
        return next;
      });
      // Cancelled, refused or saved: the control shows what the server holds now.
      const now = this.assignments()?.items.find((x) => principalKey(x) === principalKey(p));
      control.checked.set(now?.roles.includes(role) ?? false);
    }
  }

  protected async removeAll(p: Principal): Promise<void> {
    if (!p.appliesToYou) {
      const confirmed = await this.dialogs.confirm({
        title: `Remove ${p.displayName} from this workspace?`,
        message:
          p.kind === 'group'
            ? 'Members of this group lose every role it gives them in this workspace at once. Roles they hold directly stay.'
            : 'They lose every role in this workspace at once and can no longer open it.',
        confirmLabel: 'Remove access',
        tone: 'danger',
      });
      if (!confirmed) return;
    }
    await this.replace(p, [], `${p.displayName} removed from this workspace`);
  }

  protected async add(): Promise<void> {
    const catalog = this.catalog();
    const assignments = this.assignments();
    if (!catalog || !assignments) return;
    const ref = this.dialogs.open<RoleChange, AssignRolesDialogData>(AssignRolesDialog, {
      data: { catalog, assignments },
      injector: this.injector,
      width: '40rem',
    });
    const change = await firstValueFrom(ref.closed);
    if (!change) return;
    this.apply(change);
    this.announcer.announce(`Roles saved for ${change.principal.displayName}`);
  }

  /**
   * Saves `roles` for `p`. Giving up one's own role is confirmed first, with the stronger warning when it ends one's
   * own administration of the workspace; afterwards the app leaves Admin, whose pages the caller can no longer use.
   */
  private async replace(p: Principal, roles: string[], done: string): Promise<void> {
    const assignments = this.assignments();
    const catalog = this.catalog();
    if (!assignments || !catalog) return;
    const removed = p.roles.filter((r) => !roles.includes(r));
    let confirmSelf = false;
    let leavesAdmin = false;
    if (p.appliesToYou && removed.length) {
      leavesAdmin = !youStayAdmin(assignments.items, p, roles);
      const names = removed.map((r) => roleName(catalog, r)).join(', ');
      confirmSelf = await this.dialogs.confirm(
        leavesAdmin
          ? {
              title: 'Remove your own Workspace Admin role?',
              message:
                'You will no longer administer this workspace: Admin, including this page, closes for you as soon as you ' +
                'confirm. Only another Workspace Admin can give the role back.',
              confirmLabel: 'Remove my admin role',
              tone: 'danger',
            }
          : {
              title: `Remove your own role${removed.length > 1 ? 's' : ''}?`,
              message: `You lose the permissions of ${names} in this workspace at once. Only another administrator can give ${removed.length > 1 ? 'them' : 'it'} back.`,
              confirmLabel: 'Remove',
              tone: 'danger',
            },
      );
      if (!confirmSelf) return;
    }

    this.message.set(null);
    try {
      const change = await this.api.replace(
        principalRef(p),
        roles,
        assignments.version,
        confirmSelf,
      );
      this.apply(change);
      this.announcer.announce(done);
      if (leavesAdmin) {
        await this.directory.refresh(this.context.workspaceId).catch(() => undefined);
        await this.router.navigateByUrl('/workspaces');
      }
    } catch (e) {
      await this.showError(toApiError(e));
    }
  }

  private apply(change: RoleChange): void {
    this.assignments.update((a) => {
      if (!a) return a;
      const key = principalKey(change.principal);
      const exists = a.items.some((p) => principalKey(p) === key);
      const items = exists
        ? a.items.map((p) => (principalKey(p) === key ? change.principal : p))
        : [...a.items, change.principal].sort((x, y) => x.displayName.localeCompare(y.displayName));
      return {
        ...a,
        items,
        version: change.version,
        administratorPaths: change.administratorPaths,
      };
    });
  }

  private async showError(error: ApiError): Promise<void> {
    switch (error.code) {
      case 'version-conflict':
        this.message.set(
          'Someone else changed the role assignments in the meantime. The latest assignments are shown; make your change again.',
        );
        await this.refresh();
        return;
      case 'self-protection':
        this.message.set(
          'You cannot add roles to yourself or to a group you belong to. Ask another administrator.',
        );
        return;
      case 'last-administrator':
        this.message.set(
          'The workspace needs at least one Workspace Admin. Give the role to someone else first.',
        );
        await this.refresh();
        return;
      default: {
        const described = describeError(error);
        this.message.set(`${described.title}. ${described.detail}`);
      }
    }
  }

  private async refresh(): Promise<void> {
    try {
      this.assignments.set(await this.api.assignments());
    } catch {
      // The matrix stays as it was; Retry in the error state reloads it.
    }
  }

  protected focusHeading(): void {
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected readonly breakGlass = BREAK_GLASS;
  protected readonly workspaceAdmin = WORKSPACE_ADMIN;
}
