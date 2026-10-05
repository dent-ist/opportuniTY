import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../../core/api/http';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { ToastService } from '../../../ui';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { REPORT_POLL_MS } from './terms-report-page';

const WS = '/api/v1/workspaces/ws-1';
const ALEX = { userId: 'u-1', displayName: 'Alex Reviewer' };
const JAMIE = { userId: 'u-2', displayName: 'Jamie Lee' };

function summary(id: string, name: string, extra: object = {}) {
  return {
    reportId: id,
    name,
    status: 'completed',
    scope: { kind: 'savedSearch', id: 'ss-1', name: 'Responsive emails' },
    termCount: '3',
    createdBy: ALEX,
    createdAt: '2026-10-03T10:40:00Z',
    completedAt: '2026-10-03T10:42:00Z',
    jobId: `job-${id}`,
    ...extra,
  };
}

const TERMS = [
  {
    termId: 't-1',
    name: 'Termination',
    expression: 'terminat*',
    error: null,
    documentsWithHits: '40',
    documentsWithHitsIncludingFamily: '52',
    uniqueHits: '10',
    uniqueHitsIncludingFamily: '13',
  },
  {
    termId: 't-2',
    name: 'Unbalanced',
    expression: '"breach of',
    error: { code: 'unterminated-phrase', message: 'The phrase has no closing quote', position: 0 },
    documentsWithHits: null,
    documentsWithHitsIncludingFamily: null,
    uniqueHits: null,
    uniqueHitsIncludingFamily: null,
  },
  {
    termId: 't-3',
    name: 'Penalty',
    expression: 'penalt*',
    error: null,
    documentsWithHits: '1200',
    documentsWithHitsIncludingFamily: '1300',
    uniqueHits: '1150',
    uniqueHitsIncludingFamily: '1240',
  },
];

function report(id: string, extra: object = {}) {
  return {
    ...summary(id, 'Key terms'),
    snapshotId: 'snap-9',
    searchGeneration: '18432',
    indexCurrent: true,
    totals: {
      documentsInScope: '2000',
      documentsWithHits: '1220',
      documentsWithHitsIncludingFamily: '1330',
      documentsWithoutHits: '780',
    },
    terms: TERMS,
    ...extra,
  };
}

const SAVED = {
  savedSearchId: 'ss-1',
  name: 'Responsive emails',
  folderId: null,
  owner: ALEX,
  scope: 'private',
  sharedWith: [],
  lastRunAt: null,
  lastHitCount: null,
  lastHitRelation: null,
  lastRunFreshness: null,
  modifiedAt: '2026-10-01T09:00:00Z',
  version: 1,
  query: 'filetype:email',
};

const emptyPage = {
  searchId: 'search-1',
  items: [],
  page: { number: 1, size: 100, pageCount: 1, isFirst: true, isLast: true },
  total: { value: 0, relation: 'eq' },
  freshness: { asOf: '2026-10-03T10:42:00Z', current: true, servedGeneration: null },
  nextCursor: null,
  previousCursor: null,
  resultsRefreshed: false,
};

describe('Searches › Search Terms Reports (#180)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let toasts: { mock: { calls: unknown[][] } };

  async function setup(
    permissions: string[] = [PERMISSIONS.documentView, PERMISSIONS.searchExecute],
    configure: (api: FakeApi) => void = () => undefined,
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { ...ALEX, email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/search-term-reports`, {
        body: {
          items: [
            summary('str-1', 'Key terms'),
            summary('str-2', 'Privilege screen', {
              createdBy: JAMIE,
              status: 'running',
              completedAt: null,
              scope: { kind: 'workspace', id: null, name: null },
            }),
          ],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/search-term-reports/str-1`, { body: report('str-1') })
      .on('GET', `${WS}/snapshots/snap-9`, {
        body: {
          snapshotId: 'snap-9',
          name: 'Search Terms Report',
          purpose: 'report',
          status: 'ready',
          documentCount: '2000',
          selectedAt: '2026-10-03T10:41:00Z',
          createdBy: 'u-1',
          createdAt: '2026-10-03T10:41:00Z',
        },
      })
      .on('GET', `${WS}/saved-searches`, { body: { items: [SAVED], nextCursor: null } })
      .on('GET', `${WS}/saved-searches/ss-1`, { body: SAVED })
      .on('GET', `${WS}/saved-search-folders`, { body: { items: [] } })
      .on('GET', `${WS}/snapshots`, { body: { items: [], nextCursor: null } })
      .on('GET', `${WS}/fields`, { body: { items: [], nextCursor: null } })
      .on('POST', `${WS}/searches`, (req: HttpRequest<unknown>) => ({
        body: { ...emptyPage, ...(req.body as object) },
      }));
    configure(api);
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: REPORT_POLL_MS, useValue: 5 },
      ],
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

  async function settle(rounds = 6): Promise<void> {
    for (let i = 0; i < rounds; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = (el: Element = root()) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const rows = (selector = '.str__table tbody tr') => [...root().querySelectorAll(selector)];
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b) === name)!;

  it('lists the reports with scope, terms, status and who ran them', async () => {
    await setup();
    await go('/w/ws-1/searches/terms-reports');
    expect(text(root().querySelector('h1')!)).toBe('Search Terms Reports');
    expect(rows()).toHaveLength(2);
    expect(text(rows()[0])).toContain('Key terms');
    expect(text(rows()[0])).toContain('Saved search “Responsive emails”');
    expect(text(rows()[0])).toContain('Completed');
    expect(text(rows()[0])).toContain('You');
    expect(text(rows()[1])).toContain('Whole workspace');
    expect(text(rows()[1])).toContain('Running');
    expect(text(rows()[1])).toContain('Jamie Lee');
    expect(rows()[0].querySelector('a')?.getAttribute('href')).toBe(
      '/w/ws-1/searches/terms-reports/str-1',
    );
    expect(text()).toContain('New report');
    await expectNoAxeViolations(root());
  });

  it('creates a report from pasted terms with the scope pre-filled, then opens it', async () => {
    let created: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/search-term-reports`, (req) => {
        created = req;
        return { status: 202, body: report('str-9', { status: 'queued', terms: [] }) };
      }),
    );
    await go('/w/ws-1/searches/terms-reports/new?savedSearch=ss-1');
    expect(text(root().querySelector('h1')!)).toBe('New Search Terms Report');
    expect(root().querySelector<HTMLSelectElement>('select')?.value).toBe('ss-1');

    // Nothing to count yet: the summary says why and takes focus.
    root().querySelector<HTMLFormElement>('form')!.requestSubmit();
    await settle();
    const summaryEl = root().querySelector<HTMLElement>('[role="alert"]')!;
    expect(text(summaryEl)).toContain('Enter at least one term.');
    expect(document.activeElement).toBe(summaryEl);
    expect(created).toBeNull();

    const area = root().querySelector<HTMLTextAreaElement>('textarea')!;
    area.value = 'terminat*\nPenalty\tpenalt* OR liquidated\n';
    area.dispatchEvent(new Event('input'));
    await settle();
    const preview = root().querySelector('[aria-label="Terms to count"]')!;
    expect(
      rows('[aria-label="Terms to count"] tbody tr').map((r) =>
        [...r.children].map((c) => text(c)),
      ),
    ).toEqual([
      ['terminat*', 'terminat*'],
      ['Penalty', 'penalt* OR liquidated'],
    ]);
    expect(text(preview.parentElement!)).toContain('Preview: 2 terms');
    await expectNoAxeViolations(root());

    root().querySelector<HTMLFormElement>('form')!.requestSubmit();
    await settle();
    expect(created).not.toBeNull();
    const req = created! as HttpRequest<{ name: string }>;
    expect(req.body).toMatchObject({
      terms: [
        { name: 'terminat*', expression: 'terminat*' },
        { name: 'Penalty', expression: 'penalt* OR liquidated' },
      ],
      scope: { kind: 'savedSearch', id: 'ss-1' },
    });
    expect(req.body?.name).toMatch(/^Search terms – /);
    expect(req.headers.get('Idempotency-Key')).toBeTruthy();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/searches/terms-reports/str-9');
  });

  it('follows a running report through its job, then shows its results', async () => {
    let reads = 0;
    await setup(undefined, (a) =>
      a
        .on('GET', `${WS}/search-term-reports/str-5`, () => {
          reads++;
          return {
            body:
              reads < 3
                ? report('str-5', {
                    status: 'running',
                    completedAt: null,
                    totals: null,
                    snapshotId: null,
                    terms: TERMS.map((t) => ({ ...t, documentsWithHits: null })),
                  })
                : report('str-5'),
          };
        })
        .on('GET', `${WS}/jobs/job-str-5`, {
          body: {
            jobId: 'job-str-5',
            type: 'searchTermReport',
            name: 'Key terms',
            status: 'running',
            createdBy: ALEX,
            createdAt: '2026-10-03T10:40:00Z',
            updatedAt: '2026-10-03T10:40:10Z',
            committed: { done: 1, total: 4 },
            searchable: { done: 0, total: 0, state: 'notApplicable' },
            etaSeconds: 30,
          },
        }),
    );
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/searches/terms-reports/str-5');
    await settle(2);
    expect(text()).toContain('You can leave this page: the report keeps running.');
    const bar = root().querySelector('[role="progressbar"]')!;
    expect(bar.getAttribute('aria-valuenow')).toBe('25');
    expect(bar.getAttribute('aria-valuetext')).toContain('Less than a minute left');
    expect(
      [...root().querySelectorAll('a')].some(
        (a) => a.getAttribute('href') === '/w/ws-1/jobs/job-str-5',
      ),
    ).toBe(true);
    expect(text()).toContain('Counts appear when the report has finished.');
    for (let i = 0; i < 10 && reads < 3; i++) await settle(2);
    await settle();
    expect(text()).not.toContain('You can leave this page');
    expect(text()).toContain('Export CSV');
  });

  it('shows the frozen set, whose view the counts are, term errors with position, totals and links', async () => {
    await setup([PERMISSIONS.documentView, PERMISSIONS.searchExecute, 'Job.ViewAll']);
    await go('/w/ws-1/searches/terms-reports/str-1');
    expect(text(root().querySelector('h1')!)).toBe('Key terms');
    const about = root().querySelector('section[aria-labelledby="str-about"]')!;
    expect(text(about)).toContain('snap-9');
    expect(text(about)).toContain('Taken Oct 3, 2026, 10:41 AM');
    expect(text(about)).toContain('2,000 documents');
    expect(text(about)).toContain('Search generation18,432');
    expect(text(about)).toContain(
      'Counts include only the documents you could see when you ran this report.',
    );
    expect(text(about)).not.toContain('search index was not up to date');

    // Entered order; the term with a syntax error keeps its row with the error and its position.
    expect(rows().map((r) => text(r.querySelector('th')!))).toEqual([
      'Terminationterminat*',
      'Penaltypenalt*',
      'Unbalanced "breach of',
    ]);
    const bad = rows()[2];
    expect(text(bad)).toContain('Syntax error at character 1: The phrase has no closing quote.');
    expect(bad.querySelector('mark')?.textContent).toBe('"');
    expect(bad.querySelector('a')).toBeNull();
    expect(text()).toContain('1 term has a syntax error and was not counted.');

    // Counts and the links that open a term's documents.
    const cells = (r: Element) => [...r.children].map((c) => text(c));
    expect(cells(rows()[1]).slice(1)).toEqual(['1,200', '1,300', '1,150', '1,240']);
    const link = rows()[0].querySelector('td a')!;
    expect(link.getAttribute('href')).toBe('/w/ws-1/documents?termReport=str-1&term=t-1');
    expect([...root().querySelectorAll('tfoot tr')].map(cells)).toEqual([
      ['Documents with at least one hit', '1,220', '1,330', ''],
      ['Documents without hits', '780', ''],
      ['Documents in scope', '2,000', ''],
    ]);

    // Exports through the gateway.
    const hrefs = [...root().querySelectorAll('a[download]')].map((a) => a.getAttribute('href'));
    expect(hrefs).toEqual([
      `${WS}/search-term-reports/str-1/export?format=csv`,
      `${WS}/search-term-reports/str-1/export?format=xlsx`,
    ]);

    // Sorting from the header (high to low first), errors stay last.
    const header = root().querySelectorAll('thead th')[1];
    header.querySelector('button')!.click();
    await settle();
    expect(header.getAttribute('aria-sort')).toBe('descending');
    expect(rows().map((r) => text(r.querySelector('th a') ?? r.querySelector('th')!))).toEqual([
      'Penalty',
      'Termination',
      'Unbalanced "breach of',
    ]);
    await expectNoAxeViolations(root());
  });

  it('says plainly when the index was not current and when another person ran it', async () => {
    await setup(undefined, (a) =>
      a.on('GET', `${WS}/search-term-reports/str-1`, {
        body: report('str-1', { indexCurrent: false, createdBy: JAMIE }),
      }),
    );
    await go('/w/ws-1/searches/terms-reports/str-1');
    expect(text()).toContain('The search index was not up to date.');
    expect(text()).toContain('create a new report');
    expect(text()).toContain('only the documents Jamie Lee could see when they ran this report');
    // Not an admin: no generation, no delete for someone else's report.
    expect(text()).not.toContain('Search generation');
    expect(button('Delete')).toBeUndefined();
  });

  it('re-runs on the same set with an Idempotency-Key', async () => {
    const reruns: HttpRequest<unknown>[] = [];
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/search-term-reports/str-1/rerun`, (req) => {
        reruns.push(req);
        return { status: 202, body: report('str-1') };
      }),
    );
    await go('/w/ws-1/searches/terms-reports/str-1');
    button('Re-run on the same set').click();
    await settle();
    expect(reruns).toHaveLength(1);
    expect(reruns[0].headers.get('Idempotency-Key')).toBeTruthy();
    expect(toasts.mock.calls.map((c) => c[0])).toContain(
      'Re-running the report on the same frozen set.',
    );
  });

  it('opens a term in Documents and names the report and term in the search panel', async () => {
    await setup();
    await go('/w/ws-1/documents?termReport=str-1&term=t-3');
    const runs = api.requests.filter((r) => r.method === 'POST' && r.url === `${WS}/searches`);
    expect(runs).toHaveLength(1);
    expect(runs[0].body).toMatchObject({ searchTermReportId: 'str-1', termId: 't-3' });
    expect(runs[0].body).not.toHaveProperty('query');
    const panel = root().querySelector('[role="group"][aria-label="Search Terms Report term"]')!;
    expect(text(panel)).toContain('Search Terms Report: Key terms›, term Penalty');
    expect(text(panel)).toContain("Documents of the report's frozen set that match this term.");
    expect(root().querySelector<HTMLTextAreaElement>('textarea')?.value).toBe('penalt*');
  });

  it('says why a term with an error cannot be opened, and lists every document', async () => {
    await setup();
    await go('/w/ws-1/documents?termReport=str-1&term=t-2');
    expect(toasts.mock.calls.map((c) => c[0])).toContain(
      'This term has a syntax error, so it has no documents to show.',
    );
    const runs = api.requests.filter((r) => r.method === 'POST' && r.url === `${WS}/searches`);
    expect(runs.at(-1)?.body).toMatchObject({ query: '' });
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/documents');
  });

  it('starts a report from a saved search (Saved Searches › Add to new Search Terms Report)', async () => {
    await setup();
    await go('/w/ws-1/searches/saved');
    const trigger = root().querySelector<HTMLButtonElement>(
      'button[aria-label="Actions for Responsive emails"]',
    )!;
    trigger.click();
    await settle();
    const item = [
      ...document.querySelectorAll<HTMLButtonElement>('.cdk-overlay-container [role="menuitem"]'),
    ].find((b) => text(b).includes('Add to new Search Terms Report'))!;
    expect(item.getAttribute('aria-disabled')).toBeNull();
    item.click();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/searches/terms-reports/new?savedSearch=ss-1');
  });
});
