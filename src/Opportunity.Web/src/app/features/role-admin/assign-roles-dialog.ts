import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { describeError, toApiError } from '../../core/api/problem-details';
import {
  RoleAdminApi,
  RoleAssignments,
  RoleCandidate,
  RoleCatalog,
  RoleChange,
  RolePrincipalRef,
} from '../../core/security/role-admin-api';
import { Button, Checkbox, DialogLayout, Icon, TextField } from '../../ui';
import { BREAK_GLASS, inCatalogueOrder, principalKey } from './role-admin-model';

export interface AssignRolesDialogData {
  readonly catalog: RoleCatalog;
  readonly assignments: RoleAssignments;
}

/** One choice in the result list: a user, a known group, or a group name typed by the administrator. */
interface Choice {
  readonly key: string;
  readonly ref: RolePrincipalRef;
  readonly label: string;
  readonly detail: string;
}

const SEARCH_DELAY_MS = 250;

/**
 * Add user or group (E05-T08): find a user (after their first sign-in) or an identity-provider group, or type a group
 * name nobody has signed in with yet, then tick its roles. Saving replaces the roles the user or group holds (their
 * current roles start ticked), with the role-assignment version as If-Match. Closes with the change.
 */
@Component({
  selector: 'opp-assign-roles-dialog',
  imports: [Button, Checkbox, DialogLayout, Icon, TextField],
  templateUrl: './assign-roles-dialog.html',
  styleUrl: './assign-roles-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssignRolesDialog {
  protected readonly ref = inject<DialogRef<RoleChange>>(DialogRef);
  protected readonly data = inject<AssignRolesDialogData>(DIALOG_DATA);
  private readonly api = inject(RoleAdminApi);

  protected readonly text = signal('');
  protected readonly searching = signal(false);
  protected readonly candidates = signal<readonly RoleCandidate[]>([]);
  protected readonly selected = signal<Choice | null>(null);
  protected readonly roles = signal<ReadonlySet<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private timer: ReturnType<typeof setTimeout> | undefined;
  private searchSeq = 0;

  protected readonly choices = computed<readonly Choice[]>(() => {
    const found: Choice[] = this.candidates().map((c) =>
      c.kind === 'group'
        ? {
            key: `group:${c.groupName}`,
            ref: { kind: 'group', groupName: c.groupName ?? '' },
            label: c.displayName,
            detail: 'Identity-provider group',
          }
        : {
            key: `user:${c.userId}`,
            ref: { kind: 'user', userId: c.userId ?? '' },
            label: c.displayName,
            detail: c.email ?? 'User',
          },
    );
    const typed = this.text().trim();
    if (typed && !found.some((c) => c.ref.kind === 'group' && c.ref.groupName === typed)) {
      found.push({
        key: `group:${typed}`,
        ref: { kind: 'group', groupName: typed },
        label: typed,
        detail: 'Identity-provider group, by exact name',
      });
    }
    return found;
  });

  protected readonly selectedIsGroup = computed(() => this.selected()?.ref.kind === 'group');

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
    void this.search('');
  }

  protected onText(value: string): void {
    this.text.set(value);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.search(value.trim()), SEARCH_DELAY_MS);
  }

  private async search(value: string): Promise<void> {
    const seq = ++this.searchSeq;
    this.searching.set(true);
    try {
      const found = await this.api.candidates(value);
      if (seq === this.searchSeq) this.candidates.set(found);
    } catch {
      if (seq === this.searchSeq) this.candidates.set([]);
    } finally {
      if (seq === this.searchSeq) this.searching.set(false);
    }
  }

  protected current(choice: Choice): readonly string[] {
    return this.data.assignments.items.find((p) => principalKey(p) === choice.key)?.roles ?? [];
  }

  protected currentNames(choice: Choice): string {
    return this.current(choice)
      .map((k) => this.data.catalog.roles.find((r) => r.key === k)?.displayName ?? k)
      .join(', ');
  }

  protected select(choice: Choice): void {
    this.selected.set(choice);
    this.roles.set(new Set(this.current(choice)));
    this.error.set(null);
  }

  protected roleDisabled(key: string): boolean {
    if (key !== BREAK_GLASS) return false;
    if (this.selectedIsGroup()) return true;
    return !this.data.assignments.canAssignBreakGlass && !this.roles().has(key);
  }

  protected setRole(key: string, on: boolean): void {
    this.roles.update((set) => {
      const next = new Set(set);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    const choice = this.selected();
    if (!choice) {
      this.error.set('Select a user or group first.');
      return;
    }
    if (!this.roles().size && !this.current(choice).length) {
      this.error.set('Tick at least one role.');
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      const change = await this.api.replace(
        choice.ref,
        inCatalogueOrder(this.data.catalog, this.roles()),
        this.data.assignments.version,
      );
      this.ref.close(change);
    } catch (e) {
      const error = toApiError(e);
      this.error.set(
        error.code === 'self-protection'
          ? 'You cannot add roles to yourself or to a group you belong to. Ask another administrator.'
          : error.code === 'version-conflict'
            ? 'Someone else changed the role assignments in the meantime. Close this dialog and try again.'
            : error.code === 'confirmation-required'
              ? 'This removes one of your own roles. Remove it in the Users & Groups table, which asks you to confirm.'
              : `${describeError(error).title}. ${error.problem.detail ?? ''}`.trim(),
      );
    } finally {
      this.saving.set(false);
    }
  }
}
