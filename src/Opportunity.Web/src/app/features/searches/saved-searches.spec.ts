import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { ToastService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { SavedSearchesPage } from './saved-searches-page';

const WS = '/api/v1/workspaces/ws-1';

function saved(id: string, name: string, folderId: string | null, extra: object = {}) {
  return {
    savedSearchId: id,
    name,
    folderId,
    owner: { userId: 'u-1', displayName: 'Alex Reviewer' },
    scope: 'private',
    sharedWith: [],
    lastRunAt: null,
    lastHitCount: null,
    lastHitRelation: null,
    lastRunFreshness: null,
    modifiedAt: '2026-10-01T09:00:00Z',
    version: '3',
    query: `query of ${name}`,
    columns: [],
    sort: [],
    includeFamily: false,
    astVersion: 1,
    ...extra,
  };
}

const SEARCHES = [
  saved('ss-1', 'Responsive emails', 'f-1', {
    lastRunAt: '2026-10-03T10:42:00Z',
    lastHitCount: '1240',
    lastHitRelation: 'eq',
    lastRunFreshness: { state: 'current', asOf: '2026-10-03T10:42:00Z' },
  }),
  saved('ss-2', 'Privilege candidates', null, {
    owner: { userId: 'u-2', displayName: 'Jamie Lee' },
    scope: 'shared',
    sharedWith: [{ kind: 'user', id: 'u-1', displayName: 'Alex Reviewer' }],
  }),
];

describe('Searches › Saved Searches and the Documents pane (E16-T11)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let toasts: { mock: { calls: unknown[][] } };

  async function setup(
    permissions: string[] = [PERMISSIONS.documentView, PERMISSIONS.searchExecute],
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/saved-search-folders`, {
        body: {
          items: [
            { folderId: 'f-1', name: 'First pass', parentFolderId: null, version: 1 },
            { folderId: 'f-2', name: 'Hot', parentFolderId: 'f-1', version: 4 },
          ],
        },
      })
      .on('GET', `${WS}/saved-searches`, { body: { items: SEARCHES, nextCursor: null } })
      .on('GET', `${WS}/saved-searches/ss-1`, { body: SEARCHES[0] })
      .on('GET', `${WS}/saved-searches/ss-2`, { body: SEARCHES[1] })
      .on('DELETE', `${WS}/saved-searches/ss-1`, { status: 204 })
      .on('DELETE', `${WS}/saved-search-folders/f-2`, {
        status: 409,
        body: { title: 'Folder not empty', status: 409, code: 'FOLDER_NOT_EMPTY' },
      })
      .on('GET', `${WS}/snapshots`, {
        body: {
          items: [
            {
              snapshotId: 'snap-1',
              name: 'Mass Edit',
              purpose: 'bulkCoding',
              status: 'ready',
              documentCount: '42',
              createdBy: 'u-1',
              createdAt: '2026-10-03T10:42:00Z',
              selectedAt: '2026-10-03T10:42:00Z',
              materializedAt: '2026-10-03T10:42:01Z',
            },
          ],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/fields`, { body: { items: [], nextCursor: null } })
      .on('POST', `${WS}/searches`, (req: HttpRequest<unknown>) => ({
        body: {
          searchId: 'search-1',
          savedSearchId: (req.body as { savedSearchId?: string }).savedSearchId ?? null,
          items: [],
          page: { number: 1, size: 100, pageCount: 1, isFirst: true, isLast: true },
          total: { value: 0, relation: 'eq' },
          freshness: { asOf: '2026-10-03T10:42:00Z', current: true, servedGeneration: null },
          nextCursor: null,
          previousCursor: null,
          resultsRefreshed: false,
        },
      }));
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    toasts = vi.spyOn(TestBed.inject(ToastService), 'show');
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

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = (el: Element = root()) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const rows = () => [...root().querySelectorAll('.ss__table tbody tr')];
  const treeItem = (label: string) =>
    [...root().querySelectorAll<HTMLElement>('[role="treeitem"]')].find(
      (el) => el.querySelector('.tree__label')?.textContent?.trim() === label,
    )!;
  const overlayButton = (name: string) =>
    [...document.querySelectorAll<HTMLButtonElement>('.cdk-overlay-container button')].find(
      (b) => text(b) === name,
    )!;

  it('lists the visible saved searches with folder, owner, sharing, last run and hit count', async () => {
    await setup();
    await go('/w/ws-1/searches/saved');
    expect(text(root().querySelector('h1')!)).toBe('Saved Searches');
    expect(rows()).toHaveLength(2);
    expect(text(rows()[0])).toContain('Responsive emails');
    expect(text(rows()[0])).toContain('First pass');
    expect(text(rows()[0])).toContain('You');
    expect(text(rows()[0])).toContain('Private');
    expect(text(rows()[0])).toContain('1,240');
    expect(text(rows()[0])).toContain('Current as of 10:42 AM');
    expect(text(rows()[1])).toContain('Jamie Lee');
    expect(text(rows()[1])).toContain('Shared with you');
    expect(text(rows()[1])).toContain('Results filtered for you');
    expect(text(rows()[1])).toContain('Never');
    // Running opens Documents with the saved search applied.
    expect(rows()[0].querySelector('a')?.getAttribute('href')).toBe(
      '/w/ws-1/documents?savedSearch=ss-1',
    );
    // No create actions are hidden from a member with Search.Execute.
    expect(text()).toContain('New saved search');
    await expectNoAxeViolations(root());
  });

  it('filters by folder and name, and shows frozen sets apart from live searches', async () => {
    await setup();
    await go('/w/ws-1/searches/saved');
    treeItem('First pass').click();
    await settle();
    expect(rows().map((r) => text(r.querySelector('th')!))).toEqual(['Responsive emails']);
    expect(text()).toContain('Rename or move folder');

    treeItem('Not in a folder').click();
    await settle();
    expect(rows().map((r) => text(r.querySelector('th')!))).toEqual(['Privilege candidates']);

    treeItem('Frozen sets').click();
    await settle();
    expect(api.urls()).toContain(`${WS}/snapshots?limit=100`);
    expect(text()).toContain(
      'Frozen set (snapshot): 42 documents, frozen Oct 3, 2026, 10:42 AM by you',
    );
    expect(text()).toContain('keeps the documents it had when it was frozen');
    await expectNoAxeViolations(root());
  });

  it('deletes with If-Match and explains why a folder that is not empty stays', async () => {
    await setup();
    await go('/w/ws-1/searches/saved');
    const page = harness.fixture.debugElement.query(By.directive(SavedSearchesPage))
      .componentInstance as {
      remove(s: unknown): Promise<void>;
      deleteFolder(f: unknown): Promise<void>;
    };
    const removing = page.remove({ ...SEARCHES[0], version: 3 });
    await settle();
    overlayButton('Delete saved search').click();
    await removing;
    await settle();
    const del = api.requests.find((r) => r.method === 'DELETE');
    expect(del?.url).toBe(`${WS}/saved-searches/ss-1`);
    expect(del?.headers.get('If-Match')).toBe('"3"');

    const folderDelete = page.deleteFolder({
      folderId: 'f-2',
      name: 'Hot',
      parentFolderId: 'f-1',
      version: 4,
    });
    await settle();
    overlayButton('Delete folder').click();
    await folderDelete;
    await settle();
    const folderDel = api.requests.find(
      (r) => r.method === 'DELETE' && r.url === `${WS}/saved-search-folders/f-2`,
    );
    expect(folderDel?.headers.get('If-Match')).toBe('"4"');
    expect(toasts.mock.calls.map((c) => c[0]).join(' ')).toContain(
      'Only an empty folder can be deleted',
    );
  });

  it('runs ?savedSearch= in Documents by id and names it in the search panel', async () => {
    await setup();
    await go('/w/ws-1/documents?savedSearch=ss-2');
    const runs = api.requests.filter((r) => r.method === 'POST' && r.url === `${WS}/searches`);
    expect(runs.at(-1)?.body).toMatchObject({ savedSearchId: 'ss-2' });
    expect(runs.at(-1)?.body).not.toHaveProperty('query');
    const panel = root().querySelector('[role="group"][aria-label="Saved search"]')!;
    expect(text(panel)).toContain('Saved search (live): Privilege candidates');
    expect(text(panel)).toContain(
      'Shared by Jamie Lee. Results are filtered to the documents you may see.',
    );
    expect(root().querySelector<HTMLTextAreaElement>('textarea')?.value).toBe(
      'query of Privilege candidates',
    );
    // The browser pane lists folders and searches; the running one is selected.
    const pane = root().querySelector('aside[aria-labelledby="documents-browser-heading"]')!;
    expect(text(pane)).toContain('Save current search');
    const items = [...pane.querySelectorAll('[role="treeitem"]')];
    expect(items.map((i) => i.querySelector('.tree__label')?.textContent?.trim())).toEqual([
      'First pass',
      'Hot',
      'Responsive emails',
      'Privilege candidates',
    ]);
    expect(items[3].getAttribute('aria-selected')).toBe('true');
  });

  it('says so when a saved search is not available, and lists every document instead', async () => {
    await setup();
    await go('/w/ws-1/documents?savedSearch=gone');
    expect(toasts.mock.calls.map((c) => c[0]).join(' ')).toContain('no longer shared with you');
    expect(root().querySelector('[role="group"][aria-label="Saved search"]')).toBeNull();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/documents');
  });
});
