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
import { DocumentsPage } from '../documents-page';
import { FILTER_DEBOUNCE_MS } from './grid-filter';
import { LAYOUT_SAVE_DELAY_MS } from './review-grid';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { ToastService } from '../../../ui';
import { FakeResultOptions, fakePage, hit } from './grid-fixtures.testing';

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
    capabilities: { sortable: true, filterable: true, leadingWildcard: false },
    ...extra,
  };
}

const CATALOGUE = [
  field('controlnumber', 'Control Number'),
  field('date', 'Document Date', { type: 'date' }),
  field('filename', 'File Name', {
    type: 'text',
    capabilities: { sortable: true, filterable: true, leadingWildcard: true } as never,
  }),
  field('filetype', 'File Type', { capabilities: { sortable: false } as never }),
  field('extension', 'File Extension', { isHidden: true }),
  field('filesize', 'File Size', { type: 'integer' }),
  field('pagecount', 'Page Count', { type: 'integer' }),
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
      /** More API routes (views, layout) before the page opens. */
      extra?: (api: FakeApi) => void;
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
    options.extra?.(api);
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
      { query: '', sort: null, countExact: null, pageSize: 100, highlight: true },
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
    expect(text()).toContain('250 documents · Results current as of 10:42');
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
  }, 30_000); // the Documents page (with the View bar) is slow to render and check in jsdom on a loaded machine

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

  it('runs an expired search again on the page of the focused row beyond the first (Q-33, E16-T03)', async () => {
    let expired = false;
    await setup({
      search: () => {
        expired = false;
        return { body: fakePage(result, 1) };
      },
      page: (req) =>
        expired
          ? { status: 404, body: { title: 'Not found', status: 404 } }
          : {
              body: fakePage(
                result,
                Number(req.params.get('cursor')?.slice(1) ?? req.params.get('page')),
              ),
            },
    });
    button('Next').click();
    await settle();
    expect(activeText()).toBe('ACM0000101');
    expired = true;
    button('Next').click();
    await settle();
    expect(searches()).toHaveLength(2);
    expect(pageRequests()).toContain(`${PAGES}?page=2`);
    expect(activeText()).toBe('ACM0000101');
    expect(text()).toContain('Results refreshed: the search had expired and was run again.');
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
    // Review mode (E16-T03) opens on the focused document; the list stays behind it.
    expect(root().querySelector('opp-review-workspace')?.textContent).toContain('ACM0000002');
    expect(root().querySelector('.documents__list')?.hasAttribute('hidden')).toBe(true);
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

  describe('filter row (#191)', () => {
    const filterRow = () => grid().querySelector<HTMLElement>('.grid__row--filters');
    const control = (label: string) =>
      root().querySelector<HTMLElement>(`[aria-label^="Filter ${label}"]`)!;
    const stops = () => [...filterRow()!.querySelectorAll<HTMLElement>('[data-filter-stop]')];
    const type = (input: HTMLElement, value: string) => {
      (input as HTMLInputElement).value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    };
    const keyword = (query: string) =>
      (
        harness
          .routeDebugElement!.query((d) => d.name === 'opp-review-grid')
          .injector.get(DocumentsPage) as unknown as { search: { set(v: unknown): void } }
      ).search.set({ query });

    it('has a control suited to each filterable visible column, toggled by the toolbar button and remembered', async () => {
      await setup();
      expect(filterRow()).toBeNull();
      const toggle = button('Filters');
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      toggle.click();
      await settle();
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
      expect(localStorage.getItem('opp.pref.grid.filterRow')).toBe('true');
      expect(filterRow()!.getAttribute('aria-rowindex')).toBe('2');
      expect(rows()[0].getAttribute('aria-rowindex')).toBe('3');
      expect(grid().getAttribute('aria-rowcount')).toBe('252');
      const cells = [...filterRow()!.querySelectorAll('[role="gridcell"]')].map((cell) => {
        const el = cell.querySelector('input, select, button.filter__trigger');
        return el
          ? `${el.tagName.toLowerCase()} ${el.getAttribute('placeholder') ?? ''}`.trim()
          : '';
      });
      // Checkbox, Control Number (keyword), family, Date, File Name (contains), File Type (not filterable),
      // File Size, Page Count.
      expect(cells).toEqual([
        '',
        'input Starts with',
        '',
        'button',
        'input Contains',
        '',
        'button',
        'button',
      ]);
      expect(control('Document Date').getAttribute('aria-label')).toBe('Filter Document Date: Any');
      await expectNoAxeViolations(root());

      toggle.click();
      await settle();
      expect(filterRow()).toBeNull();
    }, 30_000);

    it('ANDs filters with the keyword query, keeps the sort, starts again from page 1, and Clear all restores the list', async () => {
      await setup();
      button('Filters').click();
      keyword('merger OR acquisition');
      await settle();
      [...grid().querySelectorAll<HTMLElement>('[role="columnheader"]')]
        .find((h) => h.textContent?.includes('File Name'))!
        .click();
      await settle();
      button('Next').click();
      await settle();

      const name = control('File Name');
      type(name, 'RE: (v2)');
      await settle();
      const before = searches().length;
      await settle(FILTER_DEBOUNCE_MS + 50);
      expect(searches()).toHaveLength(before + 1);
      expect(searches().at(-1)).toEqual({
        query: '(merger OR acquisition) AND filename:*RE\\:\\ \\(v2\\)*',
        sort: [{ field: 'fileName', direction: 'asc' }],
        countExact: null,
        pageSize: 100,
        highlight: true,
      });
      expect(text()).toContain('Page 1 of 3');

      type(control('Control Number'), 'ACM1');
      name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      control('Control Number').dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }),
      );
      await settle();
      expect(searches().at(-1)?.query).toBe(
        '(merger OR acquisition) AND filename:*RE\\:\\ \\(v2\\)* AND controlnumber:ACM1*',
      );
      const summary = root().querySelector('opp-filter-summary')!;
      expect(summary.textContent).toMatch(/^Filters:.*Clear all$/);
      expect(
        [...summary.querySelectorAll('.chip')].map((c) => c.getAttribute('aria-label')),
      ).toEqual([
        'Remove filter: File Name contains “RE: (v2)”',
        'Remove filter: Control Number starts with “ACM1”',
      ]);

      // A chip removes its filter even with the row closed.
      button('Filters').click();
      await settle();
      summary
        .querySelector<HTMLButtonElement>('[aria-label^="Remove filter: Control Number"]')!
        .click();
      await settle();
      expect(searches().at(-1)?.query).toBe(
        '(merger OR acquisition) AND filename:*RE\\:\\ \\(v2\\)*',
      );
      button('Clear all').click();
      await settle();
      expect(searches().at(-1)?.query).toBe('merger OR acquisition');
      expect(root().querySelector('opp-filter-summary')).toBeNull();
      expect(document.activeElement).toBe(button('Filters'));
    });

    it('edits ranges, choices and presence in a dialog', async () => {
      await setup();
      button('Filters').click();
      await settle();
      control('File Size').click();
      await settle();
      const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]')!;
      expect(dialog().getAttribute('aria-labelledby')).toBeTruthy();
      const inputs = () => [...dialog().querySelectorAll<HTMLInputElement>('input[type="text"]')];
      type(inputs()[0], '1 MB');
      type(inputs()[1], '10 KB');
      await settle();
      const apply = () =>
        [...dialog().querySelectorAll<HTMLButtonElement>('button')].find(
          (b) => b.textContent?.trim() === 'Apply',
        )!;
      apply().click();
      await settle();
      expect(dialog().textContent).toContain('"From" is after "To".');
      type(inputs()[1], '');
      apply().click();
      await settle();
      expect(document.querySelector('[role="dialog"]')).toBeNull();
      expect(searches().at(-1)?.query).toBe('filesize:[1048576 TO *]');
      expect(control('File Size').getAttribute('aria-label')).toBe('Filter File Size: from 1 MB');

      control('Document Date').click();
      await settle();
      dialog().querySelectorAll<HTMLInputElement>('input[type="radio"]')[2].click();
      await settle();
      apply().click();
      await settle();
      expect(searches().at(-1)?.query).toBe('filesize:[1048576 TO *] AND NOT date:*');
    });

    it('moves between filters with the arrow keys, clears the focused one with Escape, and leaves grid keys alone', async () => {
      await setup();
      button('Filters').click();
      await settle();
      const all = stops();
      // One Tab stop for the row.
      expect(all.map((s) => s.tabIndex)).toEqual([0, -1, -1, -1, -1, -1, -1]);
      all[0].focus();
      const press = (key: string, init: KeyboardEventInit = {}) =>
        document.activeElement!.dispatchEvent(
          new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
        );
      press('ArrowRight');
      expect(document.activeElement).toBe(all[1]);
      press('ArrowRight');
      expect(document.activeElement).toBe(all[2]);
      expect(stops().map((s) => s.tabIndex)).toEqual([-1, -1, 0, -1, -1, -1, -1]);
      press('End');
      expect(document.activeElement).toBe(all[6]);
      press('Home');
      expect(document.activeElement).toBe(all[0]);

      type(all[0], 'AC');
      (all[0] as HTMLInputElement).setSelectionRange(1, 1);
      press('ArrowRight'); // inside the text: the caret moves, not the focus
      expect(document.activeElement).toBe(all[0]);
      press(' ', { code: 'Space' });
      press('Enter', { code: 'Enter' });
      await settle();
      expect(text()).toContain('Selected: 0');
      expect(searches().at(-1)?.query).toBe('controlnumber:AC*');
      press('Escape');
      await settle();
      expect((all[0] as HTMLInputElement).value).toBe('');
      expect(searches().at(-1)?.query).toBe('');
      // The grid's own keys only act on the grid.
      expect(active()).toBe(`grid-ws-1-r0-c1`);
    });

    it('shows the server message under the filter it is about and keeps the grid', async () => {
      await setup({
        search: (req) => {
          const query = (req.body as SearchRequest).query ?? '';
          const at = query.indexOf('date:');
          return at < 0
            ? { body: fakePage(result, 1) }
            : {
                status: 400,
                body: {
                  type: 'urn:opportunity:problem:invalid-query',
                  title: 'Bad Request',
                  status: 400,
                  code: 'invalid-query',
                  queryErrors: [
                    {
                      code: 'UNKNOWN_FIELD',
                      message: "Unknown field 'date'.",
                      span: { start: at, end: at + 4 },
                    },
                  ],
                },
              };
        },
      });
      button('Filters').click();
      await settle();
      (
        harness.routeDebugElement!.query((d) => d.name === 'opp-review-grid')!
          .componentInstance as { setFilter(q: string, v: unknown): void }
      ).setFilter('date', { op: 'has' });
      await settle();
      const date = root().querySelector('opp-grid-filter[data-filter-field="date"]')!;
      expect(date.querySelector('.filter__error')?.textContent).toContain("Unknown field 'date'.");
      expect(control('Document Date').getAttribute('aria-invalid')).toBe('true');
      expect(grid()).not.toBeNull();
      expect(text()).toContain('A filter cannot be applied');
      // With the row closed the message moves to the summary line.
      button('Filters').click();
      await settle();
      expect(root().querySelector('.summary__error')?.textContent).toContain(
        "Document Date: Unknown field 'date'.",
      );
    });

    it('keeps the selection only for documents still in the filtered results', async () => {
      let filtered = false;
      await setup({
        search: (req) => {
          const query = (req.body as SearchRequest).query ?? '';
          if (!query) return { body: fakePage(result, 1) };
          filtered = true;
          // The filtered result: documents 2 and 4 only (one page).
          const page = fakePage({ total: 2, pageSize: 100 }, 1);
          return {
            body: {
              ...page,
              items: [fakePage(result, 1).items[1], fakePage(result, 1).items[3]],
            },
          };
        },
      });
      grid().focus();
      for (let i = 0; i < 3; i++) {
        grid().dispatchEvent(
          new KeyboardEvent('keydown', {
            key: ' ',
            code: 'Space',
            bubbles: true,
            cancelable: true,
          }),
        );
        key('ArrowDown');
      }
      await settle();
      expect(text()).toContain('Selected: 3');
      button('Filters').click();
      await settle();
      type(control('Control Number'), 'ACM');
      control('Control Number').dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }),
      );
      await settle();
      expect(filtered).toBe(true);
      expect(text()).toContain('Selected: 1');
    });
  });
  it('includes family, duplicates and email threads: base and expanded counts, marked rows (E09-T03)', async () => {
    await setup({
      search: (req) => {
        const body = req.body as SearchRequest;
        if (!body.expand) return { body: fakePage({ total: 2, pageSize: 100 }, 1) };
        const page = fakePage({ total: 4, pageSize: 100 }, 1);
        return {
          body: {
            ...page,
            total: { value: 2, relation: 'eq' },
            expand: body.expand,
            expanded: { family: 1, duplicates: 1, thread: 0, total: 4 },
            items: page.items.map((h, i) =>
              i === 1
                ? { ...h, expandedBy: 'family' }
                : i === 3
                  ? { ...h, expandedBy: 'duplicate' }
                  : h,
            ),
          },
        };
      },
    });
    expect(text()).toContain('2 documents');

    const include = root().querySelector<HTMLElement>('fieldset')!;
    expect(include.querySelector('legend')?.textContent).toContain('Include');
    const boxes = [...include.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(boxes.map((b) => b.closest('label')?.textContent?.trim())).toEqual([
      'Family',
      'Duplicates',
      'Email thread',
    ]);
    boxes[0].click();
    await settle();

    expect(searches().at(-1)?.expand).toEqual({ family: true, duplicates: false, thread: false });
    expect(text()).toContain('4 documents (2 hits + 1 family, 1 duplicate)');
    expect(firstCells()).toEqual([
      'ACM0000001',
      'ACM0000002 FamilyAdded as family',
      'ACM0000003',
      'ACM0000004 DuplicateAdded as duplicate',
    ]);

    // Unticking runs the hits alone again.
    boxes[0].click();
    await settle();
    expect(searches().at(-1)?.expand).toBeUndefined();
    expect(text()).toContain('2 documents');
    expect(text()).not.toContain('hits +');
    await expectNoAxeViolations(root());
  }, 30_000);

  describe('Views, columns and multi-column sort (E16-T09)', () => {
    const VIEWS = '/api/v1/workspaces/ws-1/grid-views';
    const LAYOUT = `${VIEWS}/layout`;
    const RESPONSIVENESS = field('responsiveness', 'Responsiveness', {
      type: 'singleChoice',
      storage: 'coding',
      isSystem: false,
      capabilities: { sortable: false, filterable: true, leadingWildcard: false } as never,
      choices: [
        { choiceId: 1, name: 'Responsive', isActive: true },
        { choiceId: 2, name: 'Not Responsive', isActive: true },
      ],
    } as never);
    const SHARED = {
      viewId: 'v-1',
      name: 'First pass',
      visibility: 'shared',
      owner: { userId: 'u-9', displayName: 'Admin' },
      columns: [{ field: 'filename' }, { field: 'responsiveness', width: 200, pinned: true }],
      sort: [{ field: 'fileName', direction: 'asc' }],
      modifiedAt: '2026-10-04T10:00:00Z',
      version: 1,
      canEdit: false,
    };
    let layoutPuts: unknown[];
    let created: unknown[];

    function views(
      layout: Record<string, unknown> = { viewId: null, columns: null, sort: null },
      list: unknown[] = [SHARED],
    ) {
      layoutPuts = [];
      created = [];
      return (fake: FakeApi) =>
        fake
          .on('GET', FIELDS, {
            body: { items: [...CATALOGUE, RESPONSIVENESS], nextCursor: null },
          })
          .on('GET', VIEWS, { body: { items: list } })
          .on('GET', LAYOUT, { body: { modifiedAt: null, ...layout } })
          .on('PUT', LAYOUT, (req) => {
            layoutPuts.push(req.body);
            return { body: req.body };
          })
          .on('POST', VIEWS, (req) => {
            created.push(req.body);
            const body = req.body as Record<string, unknown>;
            return {
              status: 201,
              body: {
                ...body,
                viewId: 'v-new',
                owner: { userId: 'u-1', displayName: 'Alex' },
                version: 1,
                canEdit: true,
              },
            };
          });
    }

    const headers = () =>
      [...grid().querySelectorAll<HTMLElement>('[role="columnheader"]')].map(
        (h) => h.querySelector('.grid__label')?.textContent?.trim() ?? '',
      );
    const header = (name: string) =>
      [...grid().querySelectorAll<HTMLElement>('[role="columnheader"]')].find(
        (h) => h.querySelector('.grid__label')?.textContent?.trim() === name,
      )!;
    const viewSelect = () => root().querySelector<HTMLSelectElement>('.grid__view select')!;
    const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
    const announced = () =>
      vi.mocked(TestBed.inject(LiveAnnouncer).announce).mock.calls.map((c) => String(c[0]));
    const shiftClick = (el: HTMLElement) =>
      el.dispatchEvent(new MouseEvent('click', { bubbles: true, shiftKey: true }));

    async function waitFor(check: () => unknown): Promise<void> {
      for (let i = 0; i < 200 && !check(); i++) await settle(10);
    }

    it('opens with the remembered View: its columns (values asked for), pinned columns first and its sort', async () => {
      await setup({
        extra: views({ viewId: 'v-1', columns: null, sort: null }),
        search: () => {
          const page = fakePage(result, 1);
          return {
            body: {
              ...page,
              items: page.items.map((h) => ({ ...h, fields: { responsiveness: ['1'] } })),
            },
          };
        },
      });
      // One search, already with the View's sort and the field values its columns need.
      expect(searches()).toHaveLength(1);
      expect(searches()[0].sort).toEqual([{ field: 'fileName', direction: 'asc' }]);
      expect(searches()[0].fields).toEqual(['responsiveness']);
      expect(headers()).toEqual(['', 'Control Number', '', 'Responsiveness', 'File Name']);
      expect(viewSelect().value).toBe('v-1');
      expect(rows()[0].textContent).toContain('Responsive');
      // Not sortable (a choice field): the header says why, and sorting it only explains.
      const describedBy = header('Responsiveness').getAttribute('aria-describedby')!;
      expect(document.getElementById(describedBy)?.textContent).toContain(
        'Choice fields cannot be sorted',
      );
      header('Responsiveness').click();
      await settle();
      expect(searches()).toHaveLength(1);
      expect(announced().at(-1)).toContain('Responsiveness cannot be sorted');
      expect(header('File Name').getAttribute('aria-sort')).toBe('ascending');
    });

    it('sorts by up to three columns with Shift, and remembers the layout', async () => {
      await setup({ extra: views() });
      header('File Name').click();
      await settle();
      shiftClick(header('Document Date'));
      await settle();
      shiftClick(header('File Size'));
      await settle();
      const three = [
        { field: 'fileName', direction: 'asc' },
        { field: 'documentDate', direction: 'asc' },
        { field: 'fileSize', direction: 'asc' },
      ];
      expect(searches().at(-1)?.sort).toEqual(three);
      expect(header('File Size').textContent).toContain('sort level 3, ascending');
      const count = searches().length;
      shiftClick(header('Page Count'));
      await settle();
      expect(searches()).toHaveLength(count);
      expect(announced().at(-1)).toContain('At most 3 sort levels');

      await settle(LAYOUT_SAVE_DELAY_MS + 50);
      expect(layoutPuts.at(-1)).toEqual({ viewId: null, columns: null, sort: three });

      // Shift+Enter on a sorted header flips its level, a second time removes it.
      shiftClick(header('Document Date'));
      await settle();
      expect(searches().at(-1)?.sort?.[1]).toEqual({ field: 'documentDate', direction: 'desc' });
      shiftClick(header('Document Date'));
      await settle();
      expect(searches().at(-1)?.sort).toEqual([three[0], three[2]]);
    });

    it('moves and resizes a column from its header with the keyboard', async () => {
      await setup({ extra: views() });
      grid().focus();
      key('ArrowUp');
      key('ArrowRight');
      key('ArrowRight');
      key('ArrowRight'); // File Name
      await settle();
      expect(activeText()).toContain('File Name');
      key('ArrowLeft', { ctrlKey: true, shiftKey: true });
      await settle();
      expect(headers().slice(3, 5)).toEqual(['File Name', 'Document Date']);
      expect(activeText()).toContain('File Name');
      key('ArrowRight', { shiftKey: true });
      await settle(LAYOUT_SAVE_DELAY_MS + 50);
      const put = layoutPuts.at(-1) as { columns: { field: string; width?: number }[] };
      // File Extension is hidden in this workspace, so the Default view does not show it.
      expect(put.columns.map((c) => c.field)).toEqual([
        'filename',
        'date',
        'filetype',
        'filesize',
        'pagecount',
      ]);
      expect(put.columns[0].width).toBeGreaterThanOrEqual(40);
    });

    it('chooses, reorders, pins and sorts columns in the Columns dialog', async () => {
      await setup({ extra: views() });
      button('Columns').click();
      await waitFor(dialog);
      await settle();
      const d = dialog()!;
      const items = () =>
        [...d.querySelectorAll<HTMLElement>('.cc__item')].map((i) =>
          i.querySelector('.cc__label')?.textContent?.trim(),
        );
      expect(items()).toEqual([
        'Document Date',
        'File Name',
        'File Type',
        'File Size',
        'Page Count',
      ]);
      // Alt+Arrow Up on a row moves it.
      d.querySelectorAll<HTMLElement>('.cc__item')[1].dispatchEvent(
        new KeyboardEvent('keydown', { key: 'ArrowUp', altKey: true, bubbles: true }),
      );
      await settle();
      expect(items().slice(0, 2)).toEqual(['File Name', 'Document Date']);
      d.querySelector<HTMLButtonElement>('button[aria-label="Remove File Type"]')!.click();
      await settle();
      [...d.querySelectorAll<HTMLButtonElement>('.cc__add')]
        .find((b) => b.textContent?.includes('Responsiveness'))!
        .click();
      await settle();
      expect(items().at(-1)).toBe('Responsiveness');
      expect(d.textContent).toContain('Not sortable');
      // Sort levels list the fields that cannot be sorted as unavailable.
      const level1 = d.querySelector<HTMLSelectElement>('.cc__level select')!;
      const option = [...level1.options].find((o) => o.textContent?.includes('Responsiveness'))!;
      expect(option.disabled).toBe(true);
      expect(option.textContent).toContain('cannot be sorted');
      level1.value = 'pageCount';
      level1.dispatchEvent(new Event('change'));
      await settle();
      [...d.querySelectorAll<HTMLButtonElement>('button')]
        .find((b) => b.textContent?.trim() === 'Apply')!
        .click();
      await settle();
      expect(dialog()).toBeNull();
      expect(searches().at(-1)?.sort).toEqual([{ field: 'pageCount', direction: 'asc' }]);
      expect(searches().at(-1)?.fields).toEqual(['responsiveness']);
      expect(headers().slice(3)).toEqual([
        'File Name',
        'Document Date',
        'File Size',
        'Page Count',
        'Responsiveness',
      ]);
      expect(root().querySelector('.grid__modified')?.textContent).toContain('Modified');
    }, 30_000);

    it('saves the list as a personal view; sharing needs the manage-views permission', async () => {
      await setup({ extra: views() });
      header('File Size').click();
      await settle();
      button('Views').click();
      await settle();
      [...document.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
        .find((b) => b.textContent?.includes('Save as new view'))!
        .click();
      await waitFor(dialog);
      await settle();
      const d = dialog()!;
      expect(d.querySelector<HTMLInputElement>('input[value="shared"]')!.disabled).toBe(true);
      expect(d.textContent).toContain('needs the "Manage shared views" permission');
      const name = d.querySelector<HTMLInputElement>('input:not([type="radio"])')!;
      name.value = 'Big files';
      name.dispatchEvent(new Event('input', { bubbles: true }));
      [...d.querySelectorAll<HTMLButtonElement>('button')]
        .find((b) => b.textContent?.trim() === 'Save view')!
        .click();
      await waitFor(() => !dialog());
      await settle();
      expect(created).toEqual([
        {
          name: 'Big files',
          visibility: 'personal',
          columns: ['date', 'filename', 'filetype', 'filesize', 'pagecount'].map((f) => ({
            field: f,
            width: null,
            pinned: false,
          })),
          sort: [{ field: 'fileSize', direction: 'asc' }],
        },
      ]);
      expect(viewSelect().value).toBe('v-new');
      expect(root().querySelector('.grid__modified')).toBeNull();

      // Back to the Default view: its columns and the default order.
      viewSelect().value = '';
      viewSelect().dispatchEvent(new Event('change'));
      await settle();
      expect(searches().at(-1)?.sort).toBeNull();
    }, 30_000);
  });

  describe('family groups and the duplicate indicator (E16-T10)', () => {
    // ACM1 stand-alone; family of ACM2 with two attachments; ACM5 and ACM6 attachments of a parent not in the list;
    // ACM7 has duplicates.
    const familyHits = [
      hit(1),
      hit(2, { familyId: 'fam-2', isFamilyParent: true }),
      hit(3, { familyId: 'fam-2', parentDocumentId: 'doc-2', familySequence: 1 }),
      hit(4, { familyId: 'fam-2', parentDocumentId: 'doc-2', familySequence: 2 }),
      hit(5, { familyId: 'fam-9', parentDocumentId: 'doc-9', familySequence: 1 }),
      hit(6, { familyId: 'fam-9', parentDocumentId: 'doc-9', familySequence: 2 }),
      { ...hit(7), duplicateGroupId: 'dup-7' },
    ];
    const tree = () => root().querySelector<HTMLElement>('[role="treegrid"]')!;
    const treeRows = () => [
      ...tree().querySelectorAll<HTMLElement>('[role="rowgroup"] + [role="rowgroup"] [role="row"]'),
    ];
    const describeRow = (r: HTMLElement) =>
      `${r.getAttribute('aria-level')}${r.hasAttribute('aria-expanded') ? `:${r.getAttribute('aria-expanded')}` : ''} ${r
        .querySelectorAll('[role="gridcell"]')[1]
        .textContent?.replace(/\s+/g, ' ')
        .trim()}`;

    async function grouped(): Promise<void> {
      await setup({
        result: { total: 7, pageSize: 100 },
        search: () => ({ body: { ...fakePage(result, 1), items: familyHits } }),
      });
      button('Group families').click();
      await settle();
    }

    it('sorts by Family Date and shows parents with indented attachments in a tree grid', async () => {
      await grouped();
      expect(searches().at(-1)?.sort).toEqual([{ field: 'familyDate', direction: 'asc' }]);
      expect(button('Group families').getAttribute('aria-pressed')).toBe('true');
      expect(tree().getAttribute('aria-rowcount')).toBe('-1');
      expect(treeRows().map(describeRow)).toEqual([
        '1 ACM0000001',
        '1:true ACM0000002',
        '2 ACM0000003',
        '2 ACM0000004',
        // The parent of ACM5 and ACM6 is not in the results: a placeholder without metadata stands in.
        '1:true Parent not listedParent not in these results: the attachments below belong to it.',
        '2 ACM0000005',
        '2 ACM0000006',
        '1 ACM0000007',
      ]);
      await expectNoAxeViolations(root());
    }, 30_000);

    it('collapses and expands a family with the toggle and with Left / Right on the Family column', async () => {
      await grouped();
      treeRows()[1].querySelector<HTMLButtonElement>('.grid__tree')!.click();
      await settle();
      expect(treeRows().map(describeRow).slice(0, 3)).toEqual([
        '1 ACM0000001',
        '1:false ACM0000002',
        '1:true Parent not listedParent not in these results: the attachments below belong to it.',
      ]);

      // Keyboard (the toggled row is focused): to the Family column, Right expands, Down to an attachment, Left goes
      // up to the parent, Left again collapses.
      tree().focus();
      key('ArrowRight', {}, tree()); // Control Number → Family column
      key('ArrowRight', {}, tree());
      await settle();
      expect(treeRows()[1].getAttribute('aria-expanded')).toBe('true');
      key('ArrowDown', {}, tree());
      key('ArrowLeft', {}, tree());
      await settle();
      expect(tree().getAttribute('aria-activedescendant')).toBe(`grid-ws-1-r1-c2`);
      key('ArrowLeft', {}, tree());
      await settle();
      expect(treeRows()[1].getAttribute('aria-expanded')).toBe('false');
    }, 30_000);

    it('turns grouping off when the list is sorted by another column', async () => {
      await grouped();
      const header = [...tree().querySelectorAll<HTMLElement>('[role="columnheader"]')].find(
        (h) => h.textContent?.trim() === 'File Name',
      )!;
      header.click();
      await settle();
      expect(root().querySelector('[role="treegrid"]')).toBeNull();
      expect(button('Group families').getAttribute('aria-pressed')).toBe('false');
      expect(searches().at(-1)?.sort).toEqual([{ field: 'fileName', direction: 'asc' }]);
    }, 30_000);

    it('runs the duplicate group as a new search from the duplicate indicator', async () => {
      await grouped();
      const indicator = treeRows().at(-1)!.querySelector<HTMLButtonElement>('.grid__dup')!;
      expect(indicator.textContent).toContain('Has duplicates');
      indicator.click();
      await settle();
      expect(searches().at(-1)?.query).toBe('duplicategroup:"dup-7"');
      expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe(
        'Showing: Duplicates of ACM0000007.',
      );
    }, 30_000);
  });
});
