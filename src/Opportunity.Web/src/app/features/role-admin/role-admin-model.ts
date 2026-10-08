import type { RoleAssignmentPrincipalResource } from '../../core/api/generated/models';
import type { RoleAssignments, RoleCatalog } from '../../core/security/role-admin-api';

/** Role keys the screens treat specially (ADR-015 D5.7, D6.4). */
export const WORKSPACE_ADMIN = 'WorkspaceAdmin';
export const BREAK_GLASS = 'BreakGlass';

/** The plain-language promise every role and access screen makes (E05-T08). */
export const APPLIES_IMMEDIATELY =
  'Changes apply immediately to document access: viewing, downloading and coding. Search result lists may lag briefly.';

export type Principal = RoleAssignmentPrincipalResource;

/** Stable key of a user or group row. */
export function principalKey(p: {
  readonly kind: string;
  readonly userId?: string | null;
  readonly groupName?: string | null;
}): string {
  return p.kind === 'group' ? `group:${p.groupName}` : `user:${p.userId}`;
}

/** What one cell of the assignment matrix shows and whether it can change, with the reason when it cannot. */
export interface CellState {
  readonly checked: boolean;
  /** The cell does not apply (Break-glass for a group): no control is shown. */
  readonly notApplicable: boolean;
  readonly disabled: boolean;
  readonly reason: string | null;
}

/**
 * The rules of the role-assignment API, applied ahead of time so the matrix never offers a change the server will
 * refuse: nobody adds a role to themselves (self-protection, ADR-015 D6.5); the last Workspace Admin assignment stays;
 * Break-glass is for users only and only an Installation Admin assigns it (ADR-015 D6.4). Removing is always offered
 * otherwise; giving up one's own role asks for confirmation first.
 */
export function cellState(
  principal: Principal,
  role: string,
  assignments: Pick<RoleAssignments, 'administratorPaths' | 'canAssignBreakGlass'>,
): CellState {
  const checked = principal.roles.includes(role);
  if (role === BREAK_GLASS && principal.kind === 'group') {
    return {
      checked,
      notApplicable: true,
      disabled: true,
      reason: 'Break-glass is for individual users only',
    };
  }
  if (checked) {
    if (role === WORKSPACE_ADMIN && assignments.administratorPaths <= 1) {
      return {
        checked,
        notApplicable: false,
        disabled: true,
        reason: 'The workspace needs at least one Workspace Admin',
      };
    }
    return { checked, notApplicable: false, disabled: false, reason: null };
  }
  if (principal.appliesToYou) {
    return {
      checked,
      notApplicable: false,
      disabled: true,
      reason:
        principal.kind === 'group'
          ? 'You belong to this group; another administrator must add its roles'
          : 'Another administrator must add roles to you',
    };
  }
  if (role === BREAK_GLASS && !assignments.canAssignBreakGlass) {
    return {
      checked,
      notApplicable: false,
      disabled: true,
      reason: 'Only an Installation Admin can assign Break-glass',
    };
  }
  return { checked, notApplicable: false, disabled: false, reason: null };
}

/** True when, with `roles` for `changed`, the caller still holds Workspace Admin through some assignment. */
export function youStayAdmin(
  items: readonly Principal[],
  changed: Principal,
  roles: readonly string[],
): boolean {
  const key = principalKey(changed);
  return items.some(
    (p) => p.appliesToYou && (principalKey(p) === key ? roles : p.roles).includes(WORKSPACE_ADMIN),
  );
}

/** Roles in catalogue order. */
export function inCatalogueOrder(catalog: RoleCatalog, roles: Iterable<string>): string[] {
  const set = new Set(roles);
  return catalog.roles.map((r) => r.key).filter((k) => set.has(k));
}

/** The role's display name, falling back to its key. */
export function roleName(catalog: RoleCatalog | null, key: string): string {
  return catalog?.roles.find((r) => r.key === key)?.displayName ?? key;
}

/**
 * Arrow-key movement between the checkboxes of a matrix (cells carry `data-row` / `data-col`): Left/Right within the
 * row, Up/Down within the column, Home/End to the row's ends. Disabled cells are skipped. Space toggles (native).
 */
export function moveInMatrix(event: KeyboardEvent): void {
  const input = event.target as HTMLElement | null;
  const cell = input?.closest<HTMLElement>('[data-row][data-col]');
  const table = cell?.closest('table');
  if (!cell || !table) return;
  const row = Number(cell.dataset['row']);
  const col = Number(cell.dataset['col']);
  const enabled = (r: number, c: number) =>
    table.querySelector<HTMLInputElement>(
      `[data-row="${r}"][data-col="${c}"] input[type="checkbox"]:not(:disabled)`,
    );
  const cells = Array.from(table.querySelectorAll<HTMLElement>('[data-row][data-col]'));
  const rows = Math.max(...cells.map((c) => Number(c.dataset['row']))) + 1;
  const cols = Math.max(...cells.map((c) => Number(c.dataset['col']))) + 1;
  let target: HTMLInputElement | null = null;
  const scan = (r: number, c: number, dr: number, dc: number) => {
    for (let i = r + dr, j = c + dc; i >= 0 && i < rows && j >= 0 && j < cols; i += dr, j += dc) {
      const found = enabled(i, j);
      if (found) return found;
    }
    return null;
  };
  switch (event.key) {
    case 'ArrowRight':
      target = scan(row, col, 0, 1);
      break;
    case 'ArrowLeft':
      target = scan(row, col, 0, -1);
      break;
    case 'ArrowDown':
      target = scan(row, col, 1, 0);
      break;
    case 'ArrowUp':
      target = scan(row, col, -1, 0);
      break;
    case 'Home':
      target = scan(row, -1, 0, 1);
      break;
    case 'End':
      target = scan(row, cols, 0, -1);
      break;
    default:
      return;
  }
  event.preventDefault();
  target?.focus();
}
