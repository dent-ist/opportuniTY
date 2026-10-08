import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { Injectable, Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { FakeApi as FakeSessionApi, provideFakeApi } from '../../core/api/fake-api.testing';
import type { RoleAssignmentPrincipalResource } from '../../core/api/generated/models';
import {
  AdminRestrictionClass,
  RoleAdminApi,
  RoleAssignments,
  RoleCandidate,
  RoleCatalog,
  RoleChange,
  RolePrincipalRef,
} from '../../core/security/role-admin-api';
import { WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { DialogService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { AssignRolesDialog } from './assign-roles-dialog';
import { cellState, youStayAdmin } from './role-admin-model';
import { RolesSecurityPage } from './roles-security-page';
import { UsersGroupsPage } from './users-groups-page';

const CATALOG: RoleCatalog = {
  roles: [
    {
      key: 'WorkspaceAdmin',
      displayName: 'Workspace Admin',
      permissions: ['Document.View', 'Workspace.ManageUsers'],
      usersOnly: false,
      needsInstallationAdmin: false,
    },
    {
      key: 'Reviewer',
      displayName: 'Reviewer',
      permissions: ['Document.View'],
      usersOnly: false,
      needsInstallationAdmin: false,
    },
    {
      key: 'BreakGlass',
      displayName: 'Break-glass',
      permissions: ['Document.View'],
      usersOnly: true,
      needsInstallationAdmin: true,
    },
  ],
  permissions: [
    { name: 'Document.View', description: 'Open a document.', breakGlassEligible: true },
    { name: 'Workspace.ManageUsers', description: 'Assign roles.', breakGlassEligible: false },
  ],
};

function principal(
  kind: 'user' | 'group',
  name: string,
  roles: string[],
  extra: Partial<RoleAssignmentPrincipalResource> = {},
): RoleAssignmentPrincipalResource {
  return {
    kind,
    userId: kind === 'user' ? `u-${name.split(' ')[0].toLowerCase()}` : null,
    groupName: kind === 'group' ? name : null,
    displayName: name,
    email: kind === 'user' ? `${name.split(' ')[0].toLowerCase()}@example.test` : null,
    roles,
    appliesToYou: false,
    ...extra,
  };
}

function assignments(
  items: RoleAssignmentPrincipalResource[],
  extra: Partial<RoleAssignments> = {},
): RoleAssignments {
  return {
    items,
    version: 7,
    administratorPaths: items.filter((p) => p.roles.includes('WorkspaceAdmin')).length,
    canAssignBreakGlass: false,
    ...extra,
  };
}

const CLASSES: AdminRestrictionClass[] = [
  {
    classKey: 'Privileged',
    displayName: 'Privileged',
    isBuiltIn: true,
    roles: ['WorkspaceAdmin', 'Reviewer'],
    rules: [],
    updatedAt: '2026-10-01T09:00:00Z',
    version: 3,
  },
];

@Injectable()
class FakeRoleAdminApi extends RoleAdminApi {
  static state: RoleAssignments = assignments([]);
  replaced: { principal: RolePrincipalRef; roles: string[]; version: number; confirm: boolean }[] =
    [];
  classSaves: { classKey: string; roles: string[]; version: number }[] = [];
  reject: HttpErrorResponse | null = null;

  async roles() {
    return CATALOG;
  }
  async assignments() {
    return FakeRoleAdminApi.state;
  }
  async replace(
    who: RolePrincipalRef,
    roles: readonly string[],
    version: number,
    confirm = false,
  ): Promise<RoleChange> {
    if (this.reject) throw this.reject;
    this.replaced.push({ principal: who, roles: [...roles], version, confirm });
    const key = who.kind === 'user' ? who.userId : who.groupName;
    const state = FakeRoleAdminApi.state;
    const before = state.items.find((p) => (p.userId ?? p.groupName) === key);
    const changed = before
      ? { ...before, roles: [...roles] }
      : who.kind === 'user'
        ? principal('user', 'Morgan Reyes', [...roles], { userId: who.userId })
        : principal('group', who.groupName, [...roles]);
    const items = before
      ? state.items.map((p) => (p === before ? changed : p))
      : [...state.items, changed];
    FakeRoleAdminApi.state = assignments(items, { version: version + 1 });
    return {
      principal: changed,
      version: version + 1,
      administratorPaths: FakeRoleAdminApi.state.administratorPaths,
    };
  }
  async candidates(): Promise<RoleCandidate[]> {
    return [
      {
        kind: 'user',
        userId: 'u-morgan',
        groupName: null,
        displayName: 'Morgan Reyes',
        email: 'morgan@example.test',
      },
      {
        kind: 'group',
        userId: null,
        groupName: 'cn=paralegals',
        displayName: 'cn=paralegals',
        email: null,
      },
    ];
  }
  async restrictionClasses() {
    return CLASSES;
  }
  async setClassRoles(c: AdminRestrictionClass, roles: readonly string[]) {
    if (this.reject) throw this.reject;
    this.classSaves.push({ classKey: c.classKey, roles: [...roles], version: c.version });
    return { ...c, roles: [...roles], version: c.version + 1 };
  }
}

describe('Roles, permissions and user assignment (E05-T08)', () => {
  let api: FakeRoleAdminApi;
  let root: HTMLElement;
  let confirm: ReturnType<typeof vi.fn>;

  async function mount<T>(component: Type<T>, confirmAnswer = true): Promise<void> {
    confirm = vi.fn(async () => confirmAnswer);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideRouter([]),
        provideFakeApi(new FakeSessionApi()),
        { provide: DialogService, useValue: { confirm } },
        { provide: WorkspaceDirectory, useValue: { refresh: vi.fn(async () => ({})) } },
        {
          provide: WorkspaceContext,
          useValue: {
            workspaceId: 'ws-1',
            can: () => true,
            apiUrl: (...s: string[]) => s.join('/'),
          },
        },
      ],
    });
    TestBed.overrideComponent(component, {
      set: { providers: [{ provide: RoleAdminApi, useClass: FakeRoleAdminApi }] },
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    const fixture = TestBed.createComponent(component);
    api = fixture.debugElement.injector.get(RoleAdminApi) as FakeRoleAdminApi;
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      TestBed.tick();
    }
  }

  const box = (label: string) =>
    [...root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].find((i) =>
      i.getAttribute('aria-label')?.startsWith(label),
    )!;
  // As in the browser, change detection runs after the click event, before any awaited answer comes back.
  const click = async (input: HTMLInputElement) => {
    input.click();
    TestBed.tick();
    await settle();
  };

  afterEach(() => root?.remove());

  describe('rules', () => {
    const me = principal('user', 'Alex Admin', ['WorkspaceAdmin'], { appliesToYou: true });
    const group = principal('group', 'cn=review', ['Reviewer']);

    it('never offers adding a role to oneself, removing the last admin or Break-glass to a group', () => {
      const one = { administratorPaths: 1, canAssignBreakGlass: true };
      expect(cellState(me, 'Reviewer', one)).toEqual(
        expect.objectContaining({ disabled: true, checked: false }),
      );
      expect(cellState(me, 'WorkspaceAdmin', one).reason).toBe(
        'The workspace needs at least one Workspace Admin',
      );
      expect(cellState(me, 'WorkspaceAdmin', { ...one, administratorPaths: 2 }).disabled).toBe(
        false,
      );
      expect(cellState(group, 'BreakGlass', one).notApplicable).toBe(true);
      expect(
        cellState(principal('user', 'Sam Senior', []), 'BreakGlass', {
          ...one,
          canAssignBreakGlass: false,
        }).reason,
      ).toBe('Only an Installation Admin can assign Break-glass');
      expect(cellState(group, 'Reviewer', one)).toEqual(
        expect.objectContaining({ disabled: false, checked: true }),
      );
    });

    it('knows when a change ends the caller’s own administration, through users and groups', () => {
      const viaGroup = principal('group', 'cn=admins', ['WorkspaceAdmin'], { appliesToYou: true });
      expect(youStayAdmin([me], me, [])).toBe(false);
      expect(youStayAdmin([me, viaGroup], me, [])).toBe(true);
      expect(youStayAdmin([me, viaGroup], viaGroup, ['Reviewer'])).toBe(true);
    });
  });

  describe('Users & Groups', () => {
    beforeEach(() => {
      FakeRoleAdminApi.state = assignments([
        principal('user', 'Alex Admin', ['WorkspaceAdmin'], { appliesToYou: true }),
        principal('user', 'Sam Senior', ['WorkspaceAdmin', 'Reviewer']),
        principal('group', 'cn=review', ['Reviewer']),
      ]);
    });

    it('shows an accessible matrix with row and column headers and says changes apply at once', async () => {
      await mount(UsersGroupsPage);
      const headers = [...root.querySelectorAll('thead th[scope="col"]')].map((th) =>
        th.textContent?.trim(),
      );
      expect(headers).toEqual([
        'User or group',
        'Workspace Admin',
        'Reviewer',
        'Break-glass',
        'Actions',
      ]);
      expect(
        [...root.querySelectorAll('tbody th[scope="row"]')].map(
          (th) => th.querySelector('.ra__principal-name')?.textContent,
        ),
      ).toEqual(['Alex Admin', 'Sam Senior', 'cn=review']);
      expect(root.textContent).toContain('Search result lists may lag briefly');
      expect(box('Reviewer for Alex Admin').disabled).toBe(true);
      expect(box('Reviewer for Alex Admin').getAttribute('aria-label')).toContain(
        'Another administrator must add roles to you',
      );
      expect(box('Break-glass for Sam Senior').disabled).toBe(true);
      expect(root.textContent).toContain(
        'Break-glass for cn=review: Break-glass is for individual users only',
      );
      await expectNoAxeViolations(root);
    });

    it('saves a toggle at once with the set version and moves between cells with the arrow keys', async () => {
      await mount(UsersGroupsPage);
      await click(box('Reviewer for Sam Senior'));
      expect(api.replaced).toEqual([
        {
          principal: { kind: 'user', userId: 'u-sam' },
          roles: ['WorkspaceAdmin'],
          version: 7,
          confirm: false,
        },
      ]);
      expect(box('Reviewer for Sam Senior').checked).toBe(false);

      await click(box('Reviewer for Sam Senior'));
      expect(api.replaced[1]).toEqual(
        expect.objectContaining({ roles: ['WorkspaceAdmin', 'Reviewer'], version: 8 }),
      );

      const start = box('Workspace Admin for Sam Senior');
      start.focus();
      start.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      expect(document.activeElement).toBe(box('Reviewer for Sam Senior'));
      document.activeElement!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }),
      );
      expect(document.activeElement).toBe(box('Reviewer for cn=review'));
      document.activeElement!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }),
      );
      document.activeElement!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }),
      );
      // Alex Admin's Reviewer cell is disabled (no self-assignment): Up skips past it to nothing, focus stays.
      expect(document.activeElement).toBe(box('Reviewer for Sam Senior'));
    });

    it('warns before an admin removes their own last admin role, then leaves Admin', async () => {
      FakeRoleAdminApi.state = assignments([
        principal('user', 'Alex Admin', ['WorkspaceAdmin', 'Reviewer'], { appliesToYou: true }),
        principal('user', 'Sam Senior', ['WorkspaceAdmin']),
      ]);
      await mount(UsersGroupsPage);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      await click(box('Workspace Admin for Alex Admin'));
      expect(confirm).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Remove your own Workspace Admin role?', tone: 'danger' }),
      );
      expect(api.replaced).toEqual([
        {
          principal: { kind: 'user', userId: 'u-alex' },
          roles: ['Reviewer'],
          version: 7,
          confirm: true,
        },
      ]);
      expect(navigate).toHaveBeenCalledWith('/workspaces');
    });

    it('keeps everything when the warning is cancelled', async () => {
      FakeRoleAdminApi.state = assignments([
        principal('user', 'Alex Admin', ['WorkspaceAdmin', 'Reviewer'], { appliesToYou: true }),
        principal('user', 'Sam Senior', ['WorkspaceAdmin']),
      ]);
      await mount(UsersGroupsPage, false);
      await click(box('Reviewer for Alex Admin'));
      expect(confirm).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Remove your own role?' }),
      );
      expect(api.replaced).toEqual([]);
      expect(box('Reviewer for Alex Admin').checked).toBe(true);
    });

    it('explains a conflict and shows the latest assignments', async () => {
      await mount(UsersGroupsPage);
      api.reject = new HttpErrorResponse({
        status: 412,
        error: { status: 412, title: 'Conflict', type: 'urn:opportunity:problem:version-conflict' },
      });
      await click(box('Reviewer for Sam Senior'));
      expect(root.querySelector('[role="alert"]')?.textContent).toContain(
        'Someone else changed the role assignments',
      );
      expect(box('Reviewer for Sam Senior').checked).toBe(true);
    });
  });

  describe('Add user or group', () => {
    let closed: RoleChange | undefined;

    async function mountDialog(): Promise<void> {
      FakeRoleAdminApi.state = assignments([
        principal('user', 'Alex Admin', ['WorkspaceAdmin'], { appliesToYou: true }),
      ]);
      closed = undefined;
      TestBed.configureTestingModule({
        providers: [
          { provide: RoleAdminApi, useClass: FakeRoleAdminApi },
          {
            provide: DIALOG_DATA,
            useValue: { catalog: CATALOG, assignments: FakeRoleAdminApi.state },
          },
          { provide: DialogRef, useValue: { close: (c?: RoleChange) => (closed = c) } },
        ],
      });
      const fixture = TestBed.createComponent(AssignRolesDialog);
      api = TestBed.inject(RoleAdminApi) as FakeRoleAdminApi;
      root = fixture.nativeElement as HTMLElement;
      document.body.appendChild(root);
      await settle();
    }

    it('finds a user, ticks roles and saves them with the set version', async () => {
      await mountDialog();
      const radio = [...root.querySelectorAll<HTMLInputElement>('input[type="radio"]')][0];
      expect(radio.closest('label')?.textContent).toContain('Morgan Reyes');
      radio.click();
      await settle();
      const breakGlass = [
        ...root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
      ].find((i) => i.closest('label')?.textContent?.includes('Break-glass'))!;
      expect(breakGlass.disabled).toBe(true);
      [...root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')]
        .find((i) => i.closest('label')?.textContent?.trim() === 'Reviewer')!
        .click();
      await settle();
      await expectNoAxeViolations(root);
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      expect(api.replaced).toEqual([
        {
          principal: { kind: 'user', userId: 'u-morgan' },
          roles: ['Reviewer'],
          version: 7,
          confirm: false,
        },
      ]);
      expect(closed?.principal.roles).toEqual(['Reviewer']);
    });

    it('asks for a selection before saving', async () => {
      await mountDialog();
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      expect(root.querySelector('[role="alert"]')?.textContent).toContain(
        'Select a user or group first.',
      );
      expect(api.replaced).toEqual([]);
    });
  });

  describe('Roles & Security', () => {
    beforeEach(() => {
      FakeRoleAdminApi.state = assignments([
        principal('user', 'Alex Admin', ['WorkspaceAdmin'], { appliesToYou: true }),
      ]);
    });

    it('shows the read-only permissions of each role and toggles who may see a restriction class', async () => {
      await mount(RolesSecurityPage);
      const rows = [...root.querySelectorAll('.ra__permissions tbody tr')].map((r) =>
        [...r.querySelectorAll('td')].map((td) => td.textContent?.trim()),
      );
      expect(rows).toEqual([
        ['Granted', 'Granted', 'Granted'],
        ['Granted', 'Not granted', 'Not granted'],
      ]);
      expect(root.textContent).toContain('Search result lists may lag briefly');
      expect(box('Workspace Admin may see Privileged').disabled).toBe(true);
      expect(box('Workspace Admin may see Privileged').getAttribute('aria-label')).toContain(
        'You hold this role',
      );
      expect(root.textContent).not.toContain('Break-glass may see');

      await click(box('Reviewer may see Privileged'));
      expect(api.classSaves).toEqual([
        { classKey: 'Privileged', roles: ['WorkspaceAdmin'], version: 3 },
      ]);
      expect(box('Reviewer may see Privileged').checked).toBe(false);
      await expectNoAxeViolations(root);
    });
  });
});
