import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../../core/api/fake-api.testing';
import type { FieldResource, SearchRequest } from '../../../core/api/generated/models';
import { provideOpportunityHttp } from '../../../core/api/http';
import { CommandRegistry } from '../../../core/commands';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { ToastService } from '../../../ui';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { FakeResultOptions, fakePage } from './grid-fixtures.testing';

const SEARCHES = '/api/v1/workspaces/ws-1/searches';
const PAGES = '/api/v1/workspaces/ws-1/searches/search-1/pages';
const FIELDS = '/api/v1/workspaces/ws-1/fields';

function field(queryName: string, displayName: string, extra: Partial<FieldResource> = {}) {
  return {
    fieldId: queryName,
    queryName,
    displayName,
    type: 'keyword',
    storage: 'column',
    multiValue: false,
    isSystem: true,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    reducedCapabilities: false,
    capabilities: { sortable: true },
    ...extra,
  };
}

const CATALOGUE = [
  field('controlnumber', 'Control Number'),
  field('date', 'Document Date'),
  field('filename', 'File Name'),
  field('filetype', 'File Type', { capabilities: { sortable: false } as never }),
  field('extension', 'File Extension', { isHidden: true }),
  field('filesize', 'File Size'),
  field('pagecount', 'Page Count'),
];

describe('Review grid (Documents list)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let result: FakeResultOptions;

  async function setup(
    options: {
      result?: FakeResultOptions;
      permissions?: string[];
      search?: (req: HttpRequest<unknown>) => FakeResponse;
      page?: (req: HttpRequest<unknown>) => FakeResponse;
    } = {},
  ): Promise<void> {
    result = options.result ?? { total: 250, pageSize: 100 };
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: 'a@example.test', groups: [] },
      })
      .on('GET', '/api/v1/workspaces/ws-1', {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: options.permissions ?? [PERMISSIONS.documentView, PERMISSIONS.searchExecute],
        },
      })
      .on('GET', FIELDS, {
        body: { items: CATALOGUE, nextCursor: null, total: { value: 7, relation: 'eq' } },
      })
      .on('POST', SEARCHES, options.search ?? (() => ({ body: fakePage(result, 1) })))
      .on(
        'GET',
        PAGES,
        options.page ??
          ((req) => {
            const cursor = req.params.get('cursor');
            const last = req.params.get('last') === 'true';
            const pageCount = Math.ceil(result.total / result.pageSize);
            const n = cursor
              ? Number(cursor.slice(1))
              : last
                ? pageCount
                : Number(req.params.get('page'));
            return { body: fakePage(result, n) };
          }),
      );
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
    await TestBed.inject(CommandRegistry).ready();
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const grid = () => root().querySelector<HTMLElement>('[role="grid"]')!;
  const rows = () => [
    ...grid().querySelectorAll('[role="rowgroup"] + [role="rowgroup"] [role="row"]'),
  ];
  const firstCells = () =>
    rows().map((r) => r.querySelectorAll('[role="gridcell"]')[1].textContent?.trim());
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.trim() === name,
    )!;
  const pageRequests = () => api.urls('GET').filter((u) => u.startsWith(PAGES));
  const searches = () =>
    api.requests
      .filter((r) => r.method === 'POST' && r.url === SEARCHES)
      .map((r) => r.body as SearchRequest);
  const active = () => grid().getAttribute('aria-activedescendant');
  const activeText = () => root().querySelector(`#${active()}`)?.textContent?.trim();

  function key(key: string, init: KeyboardEventInit = {}, target: Element = grid()): void {
    target.dispatchEvent(
      new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
    );
  }

  async function scrollTo(row: number): Promise<void> {
    grid().scrollTop = row * 32;
    grid().dispatchEvent(new Event('scroll'));
    await settle();
  }

  it('lists every document in an ARIA grid with counts, freshness and columns from the field catalogue', async () => {
    await setup();
    expect(searches()).toEqual([
      { query: '', sort: null, countExact: null, pageSize: 100, highlight: false },
    ]);
    expect(grid().getAttribute('aria-rowcount')).toBe('251');
    expect(grid().getAttribute('aria-colcount')).toBe('8');
    expect(grid().getAttribute('aria-multiselectable')).toBe('true');
    const headers = [...grid().querySelectorAll('[role="columnheader"]')].map((h) =>
      h.textContent?.trim(),
    );
    // Checkbox, Control Number pinned first, family indicator, then the View (hidden fields left out).
    expect(headers).toEqual([
      '',
      'Control Number',
      'Family',
      'Document Date',
      'File Name',
      'File Type',
      'File Size',
      'Page Count',
    ]);
    expect(text()).toContain('250 documents · Current as of 10:42');
    expect(text()).toContain('Page 1 of 3');
    expect(text()).toContain('Rows 1–20 of 250');
    expect(rows()[0].getAttribute('aria-rowindex')).toBe('2');
    expect(rows()[0].textContent).toContain('03/01/2024, 02:30 PM UTC');
    await expectNoAxeViolations(root());
  }, 30_000); // axe over a rendered grid is slow in jsdom on a loaded machine

  it('keeps only the visible rows and a buffer in the DOM, fetches the next page by cursor and reuses loaded pages', async () => {
    await setup({ result: { total: 1500, pageSize: 100 } });
    expect(rows().length).toBe(30); // 20 visible + 10 buffer
    for (let page = 1; page < 15; page++) await scrollTo(page * 100 - 30);
    expect(pageRequests()).toEqual(
      Array.from({ length: 14 }, (_, i) => `${PAGES}?cursor=p${i + 2}`),
    );
    expect(rows().length).toBeLessThanOrEqual(40);
    expect(firstCells()[10]).toBe('ACM0001371');

    await scrollTo(0);
    expect(firstCells()[0]).toBe('ACM0000001');
    expect(pageRequests()).toHaveLength(14); // scrolling back reads the cache
  });

  it('pages with First, Previous, Next and Last (Q-49)', async () => {
    await setup({ result: { total: 1000, pageSize: 100 } });
    expect(button('First').disabled).toBe(true);
    button('Next').click();
    await settle();
    expect(pageRequests()).toEqual([`${PAGES}?cursor=p2`]);
    expect(text()).toContain('Page 2 of 10');
    expect(activeText()).toBe('ACM0000101');

    button('Last').click();
    await settle();
    expect(pageRequests()).toContain(`${PAGES}?last=true`);
    expect(text()).toContain('Page 10 of 10');
    expect(activeText()).toBe('ACM0000901');
    expect(button('Next').disabled).toBe(true);
    expect(button('Last').disabled).toBe(true);

    button('Previous').click();
    await settle();
    expect(text()).toContain('Page 9 of 10');
    expect(activeText()).toBe('ACM0000801');

    button('First').click();
    await settle();
    expect(pageRequests()).toContain(`${PAGES}?page=1`);
    expect(text()).toContain('Page 1 of 10');
    expect(activeText()).toBe('ACM0000001');
    expect(button('Previous').disabled).toBe(true);
    // Every page was fetched once: cached pages are reused, never re-queried.
    expect(new Set(pageRequests()).size).toBe(pageRequests().length);
  });

  it('adds Last to the loaded pages when it is the next one', async () => {
    await setup({ result: { total: 200, pageSize: 100 } });
    button('Last').click();
    await settle();
    expect(pageRequests()).toEqual([`${PAGES}?last=true`]);
    expect(text()).toContain('Page 2 of 2');
    button('First').click();
    await settle();
    expect(text()).toContain('Page 1 of 2');
    expect(pageRequests()).toEqual([`${PAGES}?last=true`]); // page 1 is still loaded
  });

  it('shows a capped total as approximate and counts exactly on request (Q-32)', async () => {
    await setup({
      result: { total: 25_000, pageSize: 100, current: null },
      search: (req) => {
        const exact = (req.body as SearchRequest).countExact;
        return { body: fakePage({ ...result, cap: exact ? Infinity : 10_000 }, 1) };
      },
    });
    expect(text()).toContain('≥ 10,000 (approx.) documents');
    expect(text()).toContain('Page 1 of ≥ 100');
    expect(grid().getAttribute('aria-rowcount')).toBe('-1');
    button('Count exactly').click();
    await settle();
    expect(searches().at(-1)?.countExact).toBe(true);
    expect(text()).toContain('≈ 25,000 documents');
    expect(text()).toContain('Page 1 of 250');
  });

  it('runs the search again when it expired, keeps the focused document and says the results were refreshed (Q-33)', async () => {
    let expired = false;
    await setup({
      page: (req) =>
        expired
          ? { status: 404, body: { title: 'Not found', status: 404 } }
          : { body: fakePage(result, Number(req.params.get('cursor')!.slice(1))) },
    });
    grid().focus();
    for (let i = 0; i < 5; i++) key('ArrowDown');
    await settle();
    expect(activeText()).toBe('ACM0000006');
    expired = true;
    button('Next').click();
    await settle();
    expect(searches()).toHaveLength(2);
    expect(root().querySelector('[role="status"]')?.textContent).toContain(
      'Results refreshed: the search had expired and was run again.',
    );
    expect(activeText()).toBe('ACM0000006');
  });

  it('shows "results refreshed" when the server reopened its view', async () => {
    await setup({
      page: (req) => ({
        body: {
          ...fakePage(result, Number(req.params.get('cursor')!.slice(1))),
          resultsRefreshed: true,
        },
      }),
    });
    button('Next').click();
    await settle();
    expect(text()).toContain('Results refreshed: the list now includes recent changes.');
  });

  it('moves the focused row and cell with the keyboard', async () => {
    await setup({ result: { total: 150, pageSize: 100 } });
    grid().focus();
    expect(activeText()).toBe('ACM0000001');
    key('ArrowDown');
    key('ArrowDown');
    await settle();
    expect(activeText()).toBe('ACM0000003');
    key('PageDown');
    await settle();
    expect(activeText()).toBe('ACM0000022');
    key('End');
    await settle();
    expect(activeText()).toBe('ACM0000100');
    expect(pageRequests()).toEqual([`${PAGES}?cursor=p2`]); // End near the loaded end prefetches
    key('End');
    await settle();
    expect(activeText()).toBe('ACM0000150');
    key('Home', { ctrlKey: true });
    await settle();
    expect(activeText()).toBe('ACM0000001');
    key('ArrowRight');
    key('ArrowRight');
    await settle();
    expect(activeText()).toBe('03/01/2024, 02:30 PM UTC'); // Document Date cell
    key('ArrowUp');
    await settle();
    expect(activeText()).toBe('Document Date');
  });

  it('sorts from the column headers by mouse and keyboard', async () => {
    await setup();
    const header = (name: string) =>
      [...grid().querySelectorAll<HTMLElement>('[role="columnheader"]')].find((h) =>
        h.textContent?.includes(name),
      )!;
    header('File Name').click();
    await settle();
    expect(searches().at(-1)?.sort).toEqual([{ field: 'fileName', direction: 'asc' }]);
    expect(header('File Name').getAttribute('aria-sort')).toBe('ascending');
    key('Enter');
    await settle();
    expect(searches().at(-1)?.sort).toEqual([{ field: 'fileName', direction: 'desc' }]);
    expect(header('File Name').getAttribute('aria-sort')).toBe('descending');
    key('Enter');
    await settle();
    expect(searches().at(-1)?.sort).toBeNull();
    expect(header('File Name').hasAttribute('aria-sort')).toBe(false);

    const before = searches().length;
    header('File Type').click(); // not sortable in this workspace
    await settle();
    expect(searches()).toHaveLength(before);
  });

  it('selects rows with Space, Shift+Arrow and Ctrl+A, clears with Alt+Shift+0 and opens with Enter', async () => {
    await setup();
    grid().focus();
    const press = (code: string, init: KeyboardEventInit = {}) =>
      grid().dispatchEvent(
        new KeyboardEvent('keydown', {
          code,
          key: code === 'Space' ? ' ' : code,
          bubbles: true,
          cancelable: true,
          ...init,
        }),
      );
    press('Space');
    await settle();
    expect(text()).toContain('Selected: 1');
    key('ArrowDown', { shiftKey: true });
    await settle();
    expect(text()).toContain('Selected: 2');
    expect(rows()[1].getAttribute('aria-selected')).toBe('true');
    press('KeyA', { ctrlKey: true, key: 'a' });
    await settle();
    expect(text()).toContain('Selected: 100');
    press('Digit0', { altKey: true, shiftKey: true, key: ')' });
    await settle();
    expect(text()).toContain('Selected: 0');

    press('Enter');
    await settle();
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe(
      'Review mode is not available yet (ACM0000002).',
    );
  });

  it('shows no-access, nothing-indexed, no-match and error states', async () => {
    await setup({ permissions: [PERMISSIONS.documentView] });
    expect(searches()).toEqual([]);
    expect(text()).toContain('No access to search');
    TestBed.resetTestingModule();

    await setup({
      search: () => ({ body: { ...fakePage(result, 1), searchId: null, items: [] } }),
    });
    expect(text()).toContain('No documents to list yet');
    TestBed.resetTestingModule();

    await setup({ result: { total: 0, pageSize: 100 } });
    expect(text()).toContain('No documents match');
    TestBed.resetTestingModule();

    let fail = true;
    await setup({
      search: () =>
        fail
          ? { status: 500, body: { title: 'Boom', status: 500 } }
          : { body: fakePage(result, 1) },
    });
    expect(text()).toContain('Something went wrong');
    fail = false;
    button('Try again').click();
    await settle();
    expect(rows().length).toBeGreaterThan(0);
  });
});
