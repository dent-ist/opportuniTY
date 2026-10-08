import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, describeError, toApiError } from '../../core/api/problem-details';
import {
  AdminRestrictionClass,
  HttpRoleAdminApi,
  RoleAdminApi,
  RoleCatalog,
} from '../../core/security/role-admin-api';
import { Announcer, Checkbox, ErrorState, Icon, LoadingState } from '../../ui';
import { APPLIES_IMMEDIATELY, BREAK_GLASS, moveInMatrix, roleName } from './role-admin-model';

/**
 * Admin › Roles & Security (E05-T08; familiarity guide §2.3 "Roles & Security (restriction classes, Q-11)"): the
 * permissions each built-in role grants, as a read-only reference (grants are fixed in code, ADR-015 D5.6; custom
 * roles are post-MVP), and which roles may see documents carrying each restriction class, as a keyboard-toggleable
 * matrix that saves at once through the restriction-class API (E05-T06, If-Match, audited). Roles the administrator
 * holds cannot be changed by them (self-protection, ADR-015 D6.5).
 */
@Component({
  selector: 'opp-roles-security-page',
  imports: [Checkbox, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './roles-security-page.html',
  styleUrl: './role-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: RoleAdminApi, useClass: HttpRoleAdminApi }],
  host: { class: 'ra-page' },
})
export class RolesSecurityPage {
  private readonly api = inject(RoleAdminApi);
  private readonly announcer = inject(Announcer);

  protected readonly appliesImmediately = APPLIES_IMMEDIATELY;
  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly catalog = signal<RoleCatalog | null>(null);
  protected readonly classes = signal<readonly AdminRestrictionClass[]>([]);
  /** Roles the administrator holds (directly or through a group); null when unknown. */
  protected readonly held = signal<ReadonlySet<string> | null>(null);
  /** Cells being saved (`classKey|role`) with the state they show meanwhile. */
  protected readonly saving = signal<ReadonlyMap<string, boolean>>(new Map());
  protected readonly message = signal<string | null>(null);

  protected readonly roles = computed(() => this.catalog()?.roles ?? []);
  /** Break-glass is never granted a class: it lifts classes while active (Q-45). */
  protected readonly classRoles = computed(() => this.roles().filter((r) => r.key !== BREAK_GLASS));

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [catalog, classes] = await Promise.all([
        this.api.roles(),
        this.api.restrictionClasses(),
      ]);
      this.catalog.set(catalog);
      this.classes.set(classes);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
    try {
      const assignments = await this.api.assignments();
      this.held.set(
        new Set(assignments.items.filter((p) => p.appliesToYou).flatMap((p) => p.roles)),
      );
    } catch {
      this.held.set(null);
    }
  }

  protected granted(roleKey: string, permission: string): boolean {
    return (
      this.roles()
        .find((r) => r.key === roleKey)
        ?.permissions.includes(permission) ?? false
    );
  }

  protected classChecked(c: AdminRestrictionClass, role: string): boolean {
    return this.saving().get(`${c.classKey}|${role}`) ?? c.roles.includes(role);
  }

  protected classLocked(role: string): boolean {
    return this.held()?.has(role) ?? false;
  }

  protected classLabel(c: AdminRestrictionClass, role: string): string {
    const label = `${roleName(this.catalog(), role)} may see ${c.displayName} documents`;
    return this.classLocked(role)
      ? `${label}. You hold this role, so another administrator must change it.`
      : label;
  }

  protected onMatrixKeydown(event: KeyboardEvent): void {
    moveInMatrix(event);
  }

  protected async toggleClass(
    c: AdminRestrictionClass,
    role: string,
    on: boolean,
    control: Checkbox,
  ): Promise<void> {
    const cellKey = `${c.classKey}|${role}`;
    if (this.saving().has(cellKey)) {
      control.checked.set(!on);
      return;
    }
    // The latest state of the class (re-syncing the control below reports the value it already holds).
    c = this.classes().find((x) => x.classKey === c.classKey) ?? c;
    if (on === c.roles.includes(role)) return;
    this.saving.update((m) => new Map(m).set(cellKey, on));
    const roles = on ? [...c.roles, role] : c.roles.filter((r) => r !== role);
    this.message.set(null);
    try {
      const saved = await this.api.setClassRoles(c, roles);
      this.classes.update((list) => list.map((x) => (x.classKey === saved.classKey ? saved : x)));
      const name = roleName(this.catalog(), role);
      this.announcer.announce(
        on
          ? `${name} may now see ${c.displayName} documents`
          : `${name} may no longer see ${c.displayName} documents`,
      );
    } catch (e) {
      const error = toApiError(e);
      if (error.code === 'version-conflict') {
        this.message.set(
          'Someone else changed this restriction class in the meantime. The latest grants are shown; make your change again.',
        );
        try {
          this.classes.set(await this.api.restrictionClasses());
        } catch {
          // The matrix stays as it was.
        }
      } else if (error.code === 'self-protection') {
        this.message.set(
          'You hold this role, so another administrator must change what it may see.',
        );
      } else {
        const described = describeError(error);
        this.message.set(`${described.title}. ${described.detail}`);
      }
    } finally {
      this.saving.update((m) => {
        const next = new Map(m);
        next.delete(cellKey);
        return next;
      });
      const now = this.classes().find((x) => x.classKey === c.classKey);
      control.checked.set(now?.roles.includes(role) ?? false);
    }
  }
}
