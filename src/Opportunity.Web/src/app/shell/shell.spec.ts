import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, Injectable, inject, signal } from '@angular/core';
import { Location } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { firstValueFrom } from 'rxjs';
import { appRoutes, workspaceRoute } from '../app.routes';
import { provideAppRouting } from '../app.config';
import { FakeApi, provideFakeApi } from '../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../core/api/http';
import { ApiError } from '../core/api/problem-details';
import { SessionPrincipal, SessionService } from '../core/session/session';
import { Workspace, WorkspaceSummary } from '../core/workspace/workspace-api';
import {
  ActiveWorkspace,
  WORKSPACE_MISMATCH,
  WorkspaceContext,
} from '../core/workspace/workspace-context';
import { PERMISSIONS } from '../core/workspace/sections';
import { expectNoAxeViolations } from '../ui/testing/axe.testing';

const alex: SessionPrincipal = {
  userId: 'user-1',
  displayName: 'Alex Reviewer',
  email: 'alex@example.test',
  groups: [],
  mfa: true,
  sessionExpiresAt: null,
};

const reviewer = [PERMISSIONS.documentView];
const admin = Object.values(PERMISSIONS);

function workspace(id: string, name: string, permissions: readonly string[]): Workspace {
  return {
    workspaceId: id,
    name,
    matterNumber: `M-${id}`,
    displayTimeZone: 'UTC',
    status: 'active',
    createdAt: '2026-10-03T00:00:00.000Z',
    breakGlassActive: false,
    permissions: [...permissions],
  };
}

const acme = workspace('ws-1', 'Acme v. Widget', admin);
const beta = workspace('ws-2', 'Beta Holdings', reviewer);

/** A workspace-scoped store as features will write them: provided on a feature component. */
@Injectable()
class ProbeStore {
  readonly selection = signal<string[]>([]);
  destroyed = false;
  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
  }
}

@Component({
  template: `<h1>Probe</h1>`,
  providers: [ProbeStore],
})
class Probe {
  readonly store = inject(ProbeStore);
  readonly context = inject(WorkspaceContext);
  private readonly http = inject(HttpClient);

  load(): Promise<unknown> {
    return firstValueFrom(this.http.get(this.context.apiUrl('documents')));
  }
}

describe('Application shell', () => {
  let api: FakeApi;

  function setup(options: { signedIn?: boolean; probe?: boolean } = {}) {
    api = new FakeApi();
    api.on(
      'GET',
      '/api/v1/me',
      options.signedIn === false ? { status: 401, body: { status: 401 } } : { body: alex },
    );
    for (const ws of [acme, beta])
      api.on('GET', `/api/v1/workspaces/${ws.workspaceId}`, { body: ws });
    api.on('GET', '/api/v1/workspaces', {
      body: {
        items: [acme, beta].map(
          ({
            workspaceId,
            name,
            matterNumber,
            displayTimeZone,
            status,
            createdAt,
          }): WorkspaceSummary => ({
            workspaceId,
            name,
            matterNumber,
            displayTimeZone,
            status,
            createdAt,
          }),
        ),
        nextCursor: null,
        total: { value: 2, relation: 'eq' },
      },
    });
    api.on('GET', '/api/v1/workspaces/ws-1/documents', { body: { items: [] } });
    api.on('GET', '/api/v1/workspaces/ws-2/documents', { body: { items: [] } });
    const routes = options.probe
      ? appRoutes(workspaceRoute([{ path: 'probe', title: 'Probe', component: Probe }]))
      : undefined;
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(routes),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
      ],
    });
  }

  beforeEach(() => localStorage.clear());

  /** Navigates like a user (the address bar changes), then waits for the page to render. */
  async function open(url: string): Promise<RouterTestingHarness> {
    const harness = await RouterTestingHarness.create();
    await go(harness, url);
    return harness;
  }

  async function go(harness: RouterTestingHarness, url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await harness.fixture.whenStable();
  }

  function probe(harness: RouterTestingHarness): Probe {
    return harness.fixture.debugElement.query(By.directive(Probe)).componentInstance as Probe;
  }

  it('sends signed-out users to the sign-in page, which starts the BFF login', async () => {
    setup({ signedIn: false });
    const harness = await open('/w/ws-1/documents');
    const el = harness.routeNativeElement!;
    expect(TestBed.inject(Location).path()).toBe('/sign-in?returnUrl=%2Fw%2Fws-1%2Fdocuments');
    expect(el.querySelector('h1')?.textContent).toContain('Sign in');

    const login = vi.spyOn(TestBed.inject(SessionService), 'login').mockImplementation(() => {});
    el.querySelector<HTMLButtonElement>('button')!.click();
    expect(login).toHaveBeenCalledWith('/w/ws-1/documents');
    await expectNoAxeViolations(el, { page: true });
  });

  it('lands on the Workspaces list after sign-in, with recent workspaces and a filter', async () => {
    setup();
    const harness = await open('/');
    const el = harness.routeNativeElement!;
    expect(TestBed.inject(Location).path()).toBe('/workspaces');
    expect(document.title).toBe('Workspaces · opportuniTY');
    expect(el.querySelector('main h1')?.textContent).toContain('Workspaces');
    const rows = el.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/w/ws-1');
    await expectNoAxeViolations(el, { page: true });
  });

  it('has a skip link, banner, navigation and main landmarks and passes axe in a workspace', async () => {
    setup();
    const harness = await open('/w/ws-1');
    const el = harness.routeNativeElement!;

    const firstFocusable = el.querySelector<HTMLAnchorElement>('a, button')!;
    expect(firstFocusable.textContent).toContain('Skip to main content');
    firstFocusable.click();
    expect(document.activeElement?.id).toBe('main');

    expect(el.querySelector('header')).not.toBeNull();
    expect(el.querySelector('nav[aria-label="Workspace sections"]')).not.toBeNull();
    expect(el.querySelector('main#main h1')?.textContent).toContain('Documents');
    expect(document.title).toBe('Documents · Acme v. Widget · opportuniTY');
    await expectNoAxeViolations(el, { page: true });
  });

  it('shows the familiar workspace sections filtered by the permissions from the API', async () => {
    setup();
    const harness = await open('/w/ws-1/documents');
    const tabs = () =>
      [...harness.routeNativeElement!.querySelectorAll('nav .shell__tab')].map((t) =>
        t.textContent!.trim(),
      );
    expect(tabs()).toEqual([
      'Documents',
      'Search Terms Reports',
      'Productions',
      'Imports',
      'Exports',
      'Jobs',
      'Admin',
    ]);
    const current = harness.routeNativeElement!.querySelector('nav [aria-current="page"]');
    expect(current?.textContent?.trim()).toBe('Documents');

    await go(harness, '/w/ws-2/documents');
    expect(tabs()).toEqual(['Documents', 'Jobs']);
    expect(
      harness.routeNativeElement!.querySelector('opp-workspace-switcher button')?.textContent,
    ).toContain('Beta Holdings');
  });

  it('shows one "Not available" page for a missing workspace, a forbidden one and a forbidden section', async () => {
    setup();
    api.on('GET', '/api/v1/workspaces/ws-403', { status: 403, body: { status: 403 } });
    const harness = await RouterTestingHarness.create();
    const location = TestBed.inject(Location);
    const texts: string[] = [];
    for (const url of ['/w/ws-missing/documents', '/w/ws-403/documents', '/w/ws-2/productions']) {
      await go(harness, url);
      expect(location.path()).toBe(url); // The deep link stays in the address bar.
      const main = harness.routeNativeElement!.querySelector('main')!;
      expect(main.querySelector('h1')?.textContent).toBe('Not available');
      texts.push(main.textContent!);
    }
    expect(new Set(texts).size).toBe(1);
    // Nothing about the workspace leaks into the header.
    expect(harness.routeNativeElement!.querySelector('opp-workspace-switcher')).toBeNull();
    expect(document.title).toBe('Not available · opportuniTY');
    await expectNoAxeViolations(harness.routeNativeElement!, { page: true });
  });

  it('carries the route workspace on every call and clears workspace state on a switch', async () => {
    setup({ probe: true });
    const harness = await RouterTestingHarness.create();
    const active = TestBed.inject(ActiveWorkspace);

    await go(harness, '/w/ws-1/probe');
    const first = probe(harness);
    await first.load();
    first.store.selection.set(['DOC-0001', 'DOC-0002']);
    expect(active.id()).toBe('ws-1');

    await go(harness, '/w/ws-2/probe');
    const second = probe(harness);
    await second.load();
    expect(second).not.toBe(first);
    expect(second.store).not.toBe(first.store);
    expect(first.store.destroyed).toBe(true);
    expect(second.store.selection()).toEqual([]);
    expect(second.context.workspaceId).toBe('ws-2');
    expect(active.id()).toBe('ws-2');
    expect(api.urls().filter((u) => u.endsWith('/documents'))).toEqual([
      '/api/v1/workspaces/ws-1/documents',
      '/api/v1/workspaces/ws-2/documents',
    ]);

    // A late call from the workspace the user left never reaches the API.
    const late = await first.load().catch((e: unknown) => e);
    expect((late as ApiError).code).toBe(WORKSPACE_MISMATCH);
    expect(api.urls().filter((u) => u.endsWith('/documents')).length).toBe(2);
  });

  it('remembers recently opened workspaces for the switcher', async () => {
    setup();
    const harness = await open('/w/ws-2/documents');
    await go(harness, '/w/ws-1/documents');
    await go(harness, '/workspaces');
    await harness.fixture.whenStable();
    const recent = [...harness.routeNativeElement!.querySelectorAll('.workspaces__card-name')];
    expect(recent.map((r) => r.textContent)).toEqual(['Acme v. Widget', 'Beta Holdings']);
  });

  it('ends the session on a 401: discards workspace content and asks to sign in again', async () => {
    setup();
    const harness = await open('/w/ws-1/documents');
    api.on('GET', '/api/v1/workspaces/ws-1/documents', { status: 401, body: { status: 401 } });

    await firstValueFrom(TestBed.inject(HttpClient).get('/api/v1/workspaces/ws-1/documents')).catch(
      () => undefined,
    );
    harness.detectChanges();
    await harness.fixture.whenStable();

    expect(TestBed.inject(SessionService).status()).toBe('expired');
    expect(TestBed.inject(ActiveWorkspace).current()).toBeNull();
    const main = harness.routeNativeElement!.querySelector('main')!;
    expect(main.querySelector('opp-workspace-shell')).toBeNull();
    expect(main.querySelector('h1')?.textContent).toContain('Your session has ended');
    const dialog = document.querySelector('[role="alertdialog"]');
    expect(dialog?.textContent).toContain('Sign in again');
  });

  it('shows the error page with a retry when the session cannot be checked', async () => {
    setup();
    let fail = true;
    api.on('GET', '/api/v1/me', () =>
      fail ? { status: 503, body: { title: 'Unavailable', status: 503 } } : { body: alex },
    );
    const harness = await open('/workspaces');
    const el = harness.routeNativeElement!;
    expect(el.querySelector('h1')?.textContent).toBe('Something went wrong');
    expect(TestBed.inject(Location).path()).toBe('/workspaces');

    fail = false;
    el.querySelector<HTMLButtonElement>('.page__actions button')!.click();
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement!.querySelector('main h1')?.textContent).toContain(
      'Workspaces',
    );
  });

  it('moves focus to the new page heading after navigation', async () => {
    setup();
    const harness = await open('/w/ws-1/documents');
    await go(harness, '/w/ws-1/jobs');
    await harness.fixture.whenStable();
    expect(document.activeElement?.tagName).toBe('H1');
    expect(document.activeElement?.textContent).toBe('Jobs');
  });

  it('signs out through the user menu', async () => {
    setup();
    const harness = await open('/workspaces');
    const logout = vi.spyOn(TestBed.inject(SessionService), 'logout').mockResolvedValue();
    harness.routeNativeElement!.querySelector<HTMLButtonElement>('opp-user-menu button')!.click();
    harness.detectChanges();
    const items = [...document.querySelectorAll<HTMLElement>('[role="menuitem"]')];
    expect(items.map((i) => i.textContent?.trim())).toEqual([
      'Keyboard shortcuts…',
      'About opportuniTY',
      'Sign out',
    ]);
    items[2].click();
    expect(logout).toHaveBeenCalled();
  });

  it('shows the licence notice and legal disclaimer on the About page', async () => {
    setup();
    const harness = await open('/about');
    const text = harness.routeNativeElement!.querySelector('main')!.textContent!;
    expect(text).toContain('MIT License');
    expect(text).toContain('not legal advice');
    expect(text).not.toMatch(/relativity|nuix|concordance/i);
    expect(TestBed.inject(Router).url).toBe('/about');
  });
});
