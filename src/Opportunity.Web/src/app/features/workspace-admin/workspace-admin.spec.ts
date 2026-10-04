import { HttpRequest } from '@angular/common/http';
import { Location } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { firstValueFrom } from 'rxjs';
import { provideAppRouting } from '../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { SessionService } from '../../core/session/session';
import { INSTALLATION_PERMISSIONS, PERMISSIONS } from '../../core/workspace/sections';
import { DialogService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { DeleteWorkspaceDialog, type DeleteWorkspaceData } from './delete-workspace-dialog';
import {
  DEFAULT_DELETION_SUMMARY,
  DELETION_NOT_AVAILABLE,
  NotYetAvailableDeletion,
} from './workspace-deletion';
import { timeZoneOptions, toWrite, validateDraft } from './workspace-form';

const ALL = Object.values(PERMISSIONS);

function workspace(id: string, name: string, permissions: readonly string[], extra = {}) {
  return {
    workspaceId: id,
    name,
    matterNumber: 'M-1',
    displayTimeZone: 'UTC',
    status: 'active',
    createdAt: '2026-10-03T00:00:00.000Z',
    updatedAt: '2026-10-03T00:00:00.000Z',
    breakGlassActive: false,
    storageProfile: 'default',
    version: 3,
    permissions: [...permissions],
    searchPlacement: null,
    ...extra,
  };
}

const page = (items: unknown[], total = items.length) => ({
  body: { items, nextCursor: null, total: { value: total, relation: 'eq' } },
});

describe('Workspace management (E04-T07)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    options: {
      installationPermissions?: string[];
      mfa?: boolean;
      permissions?: string[];
      create?: (req: HttpRequest<unknown>) => FakeResponse;
      update?: (req: HttpRequest<unknown>) => FakeResponse;
      newWorkspace?: 'empty' | 'ready';
    } = {},
  ): Promise<void> {
    const ws1 = workspace('ws-1', 'Acme v. Widget', options.permissions ?? ALL, {
      searchPlacement: { kind: 'shared', projectionGeneration: 12, state: 'active' },
    });
    const created = workspace('ws-new', 'Gamma Matter', ALL);
    const ready = options.newWorkspace === 'ready';
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: {
          userId: 'user-1',
          displayName: 'Alex Admin',
          email: null,
          groups: [],
          mfa: options.mfa ?? true,
          sessionExpiresAt: null,
          installationPermissions: options.installationPermissions ?? [
            INSTALLATION_PERMISSIONS.manageWorkspaces,
          ],
        },
      })
      .on('GET', '/api/v1/workspaces', page([ws1]))
      .on('GET', '/api/v1/workspaces/ws-1', { body: ws1 })
      .on(
        'PUT',
        '/api/v1/workspaces/ws-1',
        options.update ?? ((req) => ({ body: { ...ws1, ...(req.body as object), version: 4 } })),
      )
      .on('POST', '/api/v1/workspaces', options.create ?? { status: 201, body: created })
      .on('GET', '/api/v1/workspaces/ws-new', { body: created })
      .on('GET', '/api/v1/workspaces/ws-new/imports', page(ready ? [{ importId: 'imp-1' }] : []))
      .on(
        'GET',
        '/api/v1/workspaces/ws-new/fields',
        page([
          { fieldId: 1, isSystem: true },
          ...(ready ? [{ fieldId: 100, isSystem: false }] : []),
        ]),
      )
      .on(
        'GET',
        '/api/v1/workspaces/ws-new/coding-layouts',
        page([
          {
            layoutId: 'l-1',
            name: 'Default',
            isDefault: true,
            sections: ready ? [{ sectionId: 's', title: 'S', fields: [{ fieldId: 100 }] }] : [],
          },
        ]),
      )
      .on(
        'GET',
        '/api/v1/workspaces/ws-new/members',
        page([{ assignmentId: 'a-1' }], ready ? 3 : 1),
      );
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    localStorage.clear();
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((e) => e.remove()));

  const root = () => harness.routeNativeElement as HTMLElement;
  const squash = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const text = () => squash(root());
  const dialog = () => document.querySelector<HTMLElement>('[role="dialog"], [role="alertdialog"]');
  const buttonIn = (scope: ParentNode, name: string) =>
    [...scope.querySelectorAll<HTMLButtonElement>('button')].find((b) => squash(b) === name);
  const type = (input: HTMLInputElement | HTMLSelectElement, value: string) => {
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
  };
  const field = (scope: ParentNode, label: string) => {
    const l = [...scope.querySelectorAll('label')].find((x) => squash(x).startsWith(label))!;
    return scope.querySelector<HTMLInputElement>(`#${l.getAttribute('for')}`)!;
  };

  describe('New workspace', () => {
    it('is offered only to users who may create workspaces', async () => {
      await setup({ installationPermissions: [] });
      await go('/workspaces');
      expect(buttonIn(root(), 'New workspace')).toBeUndefined();
      expect(text()).toContain('Status');
      expect(text()).toContain('Time zone');
    });

    it('creates the workspace and lands on its setup checklist', async () => {
      await setup();
      await go('/workspaces');
      buttonIn(root(), 'New workspace')!.click();
      await settle();
      const d = dialog()!;
      expect(squash(d.querySelector('h2'))).toBe('New workspace');
      await expectNoAxeViolations(d);

      // Required name is checked before any request, and focus moves to it.
      buttonIn(d, 'Create workspace')!.click();
      await settle();
      expect(squash(d)).toContain('Enter a workspace name.');
      expect(document.activeElement).toBe(field(d, 'Workspace name'));
      expect(api.urls('POST')).toEqual([]);

      type(field(d, 'Workspace name'), '  Gamma Matter ');
      type(field(d, 'Matter number'), '2026-07');
      type(field(d, 'Display time zone') as unknown as HTMLSelectElement, 'Europe/Berlin');
      await settle();
      buttonIn(d, 'Create workspace')!.click();
      await settle();

      const post = api.requests.find((r) => r.method === 'POST')!;
      expect(post.body).toEqual({
        name: 'Gamma Matter',
        matterNumber: '2026-07',
        displayTimeZone: 'Europe/Berlin',
      });
      expect(TestBed.inject(Location).path()).toBe('/w/ws-new/admin/setup');
      expect(dialog()).toBeNull();
      expect(squash(root().querySelector('h1'))).toBe('Set up Gamma Matter');
    });

    it('explains the MFA requirement up front and signs in again with MFA', async () => {
      await setup({ mfa: false });
      await go('/workspaces');
      const stepUp = vi
        .spyOn(TestBed.inject(SessionService), 'stepUp')
        .mockImplementation(() => {});
      buttonIn(root(), 'New workspace')!.click();
      await settle();
      const d = dialog()!;
      expect(squash(d)).toContain('Multi-factor sign-in required');
      expect(d.querySelector('input')).toBeNull();
      buttonIn(d, 'Sign in with MFA')!.click();
      expect(stepUp).toHaveBeenCalledWith('/workspaces?new=true', null);
    });

    it('switches to the MFA explanation when the API asks for a step-up', async () => {
      await setup({
        create: () => ({
          status: 403,
          body: { status: 403, code: 'step-up-required', stepUpUrl: '/bff/login?stepUp=true' },
        }),
      });
      await go('/workspaces');
      const stepUp = vi
        .spyOn(TestBed.inject(SessionService), 'stepUp')
        .mockImplementation(() => {});
      buttonIn(root(), 'New workspace')!.click();
      await settle();
      const d = dialog()!;
      type(field(d, 'Workspace name'), 'Gamma');
      buttonIn(d, 'Create workspace')!.click();
      await settle();
      expect(squash(d)).toContain('Multi-factor sign-in required');
      buttonIn(d, 'Sign in with MFA')!.click();
      expect(stepUp).toHaveBeenCalledWith('/workspaces?new=true', '/bff/login?stepUp=true');
    });

    it('shows server validation next to the field and a plain message for a refused create', async () => {
      let answer: FakeResponse = {
        status: 400,
        body: { status: 400, code: 'validation', errors: { displayTimeZone: ['Unknown zone.'] } },
      };
      await setup({ create: () => answer });
      await go('/workspaces');
      buttonIn(root(), 'New workspace')!.click();
      await settle();
      const d = dialog()!;
      type(field(d, 'Workspace name'), 'Gamma');
      buttonIn(d, 'Create workspace')!.click();
      await settle();
      expect(squash(d)).toContain('Unknown zone.');

      answer = { status: 403, body: { status: 403, code: 'forbidden' } };
      buttonIn(d, 'Create workspace')!.click();
      await settle();
      expect(squash(d)).toContain('You cannot create workspaces');
    });

    it('reopens after the MFA sign-in returns to /workspaces?new=true', async () => {
      await setup();
      await go('/workspaces?new=true');
      expect(squash(dialog()?.querySelector('h2'))).toBe('New workspace');
      expect(TestBed.inject(Location).path()).toBe('/workspaces');
    });
  });

  describe('Setup checklist', () => {
    it('lists Import → Fields → Coding layouts → Users with links and honest coming-soon notes', async () => {
      await setup();
      await go('/w/ws-new/admin/setup');
      const steps = [...root().querySelectorAll('ol > li')];
      expect(steps.map((s) => squash(s.querySelector('h2')))).toEqual([
        'Import documents',
        'Fields',
        'Coding layouts',
        'Users',
      ]);
      expect(steps.every((s) => squash(s).includes('To do'))).toBe(true);
      expect(text()).toContain('0 of 4 steps done');
      expect(steps[0].querySelector('a')?.getAttribute('href')).toBe('/w/ws-new/imports/new');
      expect(squash(steps[0])).not.toContain('coming soon');
      expect(steps[1].querySelector('a')?.getAttribute('href')).toBe('/w/ws-new/admin/fields');
      expect(squash(steps[1])).toContain('Page coming soon');
      expect(squash(steps[3])).toContain('Only the workspace creator has a role so far');
      await expectNoAxeViolations(root());
    });

    it('ticks steps off when the workspace has them', async () => {
      await setup({ newWorkspace: 'ready' });
      await go('/w/ws-new/admin/setup');
      const steps = [...root().querySelectorAll('ol > li')];
      expect(steps.every((s) => squash(s).includes('Done'))).toBe(true);
      expect(text()).toContain('4 of 4 steps done');
      expect(squash(steps[1])).toContain('1 custom field');
      expect(squash(steps[3])).toContain('3 role assignments');
    });

    it('does not check steps the user may not read', async () => {
      await setup({ permissions: [PERMISSIONS.manageSecurity, PERMISSIONS.documentView] });
      await go('/w/ws-1/admin/setup');
      const steps = [...root().querySelectorAll('ol > li')];
      expect(squash(steps[0])).toContain('You cannot check this step');
      expect(steps[0].querySelector('a')).toBeNull();
      expect(api.urls().some((u) => u.includes('/imports') || u.includes('/members'))).toBe(false);
    });
  });

  describe('Workspace Settings', () => {
    it('edits name, matter number and time zone with If-Match, and shows search placement read-only', async () => {
      await setup();
      await go('/w/ws-1/admin/settings');
      expect(squash(root().querySelector('h1'))).toBe('Workspace Settings');
      expect(text()).toContain('Shared index');
      expect(text()).toMatch(/Projection generation\s*12/);
      expect(text()).toContain('ws-1');
      const save = buttonIn(root(), 'Save changes')!;
      expect(save.disabled).toBe(true);

      type(field(root(), 'Workspace name'), 'Acme v. Widget (2026)');
      await settle();
      expect(save.disabled).toBe(false);
      save.click();
      await settle();
      const put = api.requests.find((r) => r.method === 'PUT')!;
      expect(put.headers.get('If-Match')).toBe('"3"');
      expect(put.body).toEqual({
        name: 'Acme v. Widget (2026)',
        matterNumber: 'M-1',
        displayTimeZone: 'UTC',
        storageProfile: 'default',
      });
      expect(save.disabled).toBe(true);
      await expectNoAxeViolations(root());
    });

    it('explains a version conflict and reloads', async () => {
      await setup({
        update: () => ({ status: 412, body: { status: 412, code: 'version-conflict' } }),
      });
      await go('/w/ws-1/admin/settings');
      type(field(root(), 'Matter number'), 'M-2');
      await settle();
      buttonIn(root(), 'Save changes')!.click();
      await settle();
      expect(text()).toContain('Changed by someone else');
      buttonIn(root(), 'Reload settings')!.click();
      await settle();
      expect(field(root(), 'Matter number').value).toBe('M-1');
    });

    it('shows the deletion entry point disabled with the reason and calls no deletion API', async () => {
      await setup();
      await go('/w/ws-1/admin/settings');
      const del = buttonIn(root(), 'Delete workspace…')!;
      expect(del.disabled).toBe(true);
      const reason = document.getElementById(del.getAttribute('aria-describedby')!)!;
      expect(squash(reason)).toBe(DELETION_NOT_AVAILABLE);
      expect(api.requests.some((r) => r.method === 'DELETE' || r.url.includes('delet'))).toBe(
        false,
      );
    });
  });

  describe('Delete workspace dialog (ready for #166/#167)', () => {
    async function openDialog(data: DeleteWorkspaceData) {
      await setup();
      await go('/workspaces');
      const ref = TestBed.inject(DialogService).open<boolean, DeleteWorkspaceData>(
        DeleteWorkspaceDialog,
        { data, role: 'alertdialog' },
      );
      await settle();
      return ref;
    }

    it('is blocked with an explanation under a preservation lock', async () => {
      await openDialog({
        workspaceName: 'Acme v. Widget',
        availability: {
          kind: 'locked',
          lock: {
            placedBy: 'Dana Counsel',
            placedAt: '2026-09-01T00:00:00Z',
            reason: 'Litigation hold',
          },
        },
      });
      const d = dialog()!;
      expect(squash(d.querySelector('h2'))).toBe('Deletion blocked');
      expect(squash(d)).toContain('is under a preservation lock');
      expect(squash(d)).toContain('Dana Counsel');
      expect(buttonIn(d, 'Request deletion')).toBeUndefined();
      expect(d.querySelector('input')).toBeNull();
      await expectNoAxeViolations(d);
    });

    it('needs the exact workspace name and lists what is removed and kept', async () => {
      const ref = await openDialog({
        workspaceName: 'Acme v. Widget',
        availability: { kind: 'allowed', summary: DEFAULT_DELETION_SUMMARY },
      });
      const d = dialog()!;
      expect(squash(d)).toContain('Will be removed');
      expect(squash(d)).toContain('Search index data');
      expect(squash(d)).toContain('Audit trail');
      const confirm = buttonIn(d, 'Request deletion')!;
      expect(confirm.disabled).toBe(true);
      type(d.querySelector('input')!, 'acme v. widget');
      await settle();
      expect(confirm.disabled).toBe(true);
      type(d.querySelector('input')!, 'Acme v. Widget');
      await settle();
      expect(confirm.disabled).toBe(false);
      await expectNoAxeViolations(d);
      const closed = firstValueFrom(ref.closed);
      confirm.click();
      expect(await closed).toBe(true);
    });

    it('is not available in this version and never calls an API', async () => {
      const deletion = new NotYetAvailableDeletion();
      expect(await deletion.availability()).toEqual({
        kind: 'unavailable',
        reason: DELETION_NOT_AVAILABLE,
      });
      await expect(deletion.request()).rejects.toThrow(DELETION_NOT_AVAILABLE);
    });
  });

  describe('form rules', () => {
    it('validates like the API and trims the request', () => {
      expect(validateDraft({ name: ' ', matterNumber: '', displayTimeZone: 'UTC' })).toEqual({
        name: 'Enter a workspace name.',
      });
      expect(
        validateDraft({
          name: 'x'.repeat(201),
          matterNumber: 'y'.repeat(101),
          displayTimeZone: '',
        }),
      ).toEqual({
        name: 'Use at most 200 characters.',
        matterNumber: 'Use at most 100 characters.',
        displayTimeZone: 'Choose a time zone.',
      });
      expect(toWrite({ name: ' A ', matterNumber: '  ', displayTimeZone: 'UTC' })).toEqual({
        name: 'A',
        matterNumber: null,
        displayTimeZone: 'UTC',
      });
    });

    it('offers UTC first and keeps a stored zone the browser does not list', () => {
      const options = timeZoneOptions('Etc/Unknown');
      expect(options[0].value).toBe('UTC');
      expect(options.map((o) => o.value)).toContain('Etc/Unknown');
      expect(options.filter((o) => o.value === 'UTC').length).toBe(1);
    });
  });
});
