import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../../core/api/http';
import { CommandRegistry } from '../../../core/commands';
import { JOB_POLL_MS } from '../../../core/jobs/job-feed';
import { jobSummary } from '../../../core/jobs/job-fixtures.testing';
import {
  type IndexFreshness,
  SearchFreshnessApi,
  toServedFreshness,
} from '../../../core/search/search-freshness';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import type { BulkJob } from '../mass-edit/bulk-coding-api';
import { fakePage } from '../grid/grid-fixtures.testing';
import { FRESHNESS_POLL_MS, FreshnessMonitor } from './freshness-monitor';
import { bannerText, fromBulkJob, fromSummary } from './pending-jobs';

const WS = '/api/v1/workspaces/ws-1';

function index(overrides: Partial<IndexFreshness> = {}): IndexFreshness {
  return {
    state: 'updating',
    indexedThroughGeneration: '18432',
    latestGeneration: '18517',
    pendingChanges: 85,
    lagSeconds: 12,
    asOf: new Date().toISOString(),
    ...overrides,
  };
}

describe('saved but not yet searchable jobs (E16-T07)', () => {
  const finished = jobSummary({
    jobId: 'job-9',
    type: 'bulkCoding',
    name: 'Mass Edit – Responsiveness',
    status: 'completed',
    createdBy: { userId: 'u-1', displayName: 'Alex' },
    committed: { done: 10, total: 10 },
    searchable: { done: 63, total: 100, state: 'catchingUp', jobGeneration: '18517' },
  });

  it('shows the reviewer’s finished bulk coding and import jobs until they are searchable', () => {
    const entry = fromSummary(finished, 'u-1')!;
    expect(entry).toEqual({
      jobId: 'job-9',
      title: 'Mass Edit',
      progress: 0.63,
      jobGeneration: '18517',
    });
    expect(bannerText(entry, 'en-US')).toBe('Mass Edit saved · Search index updating… (63%)');
    const overlay = fromSummary(
      {
        ...finished,
        type: 'import',
        name: 'VOL002 overlay',
        searchable: { done: 0, total: 0, state: 'pending' },
      },
      'u-1',
    )!;
    expect(bannerText(overlay, 'en-US')).toBe(
      'Import “VOL002 overlay” saved · Search index updating…',
    );
    // Someone else's job, a running job, a failed one, an export and a searchable one: no banner.
    expect(fromSummary(finished, 'u-2')).toBeNull();
    expect(fromSummary({ ...finished, status: 'running' }, 'u-1')).toBeNull();
    expect(fromSummary({ ...finished, status: 'failed' }, 'u-1')).toBeNull();
    expect(fromSummary({ ...finished, type: 'export' }, 'u-1')).toBeNull();
    expect(
      fromSummary({ ...finished, searchable: { done: 100, total: 100, state: 'current' } }, 'u-1'),
    ).toBeNull();
  });

  it('reads Mass Edit jobs followed from the page the same way', () => {
    const job: BulkJob = {
      jobId: 'job-1',
      status: 'completed',
      statusReason: null,
      applied: 5,
      unchanged: 0,
      skipped: 0,
      failed: 0,
      excluded: 0,
      indexTasksApplied: 1,
      indexTasksTotal: 4,
      searchable: false,
      jobGeneration: null,
    };
    expect(fromBulkJob(job)).toEqual({
      jobId: 'job-1',
      title: 'Mass Edit',
      progress: 0.25,
      jobGeneration: null,
    });
    expect(fromBulkJob({ ...job, searchable: true })).toBeNull();
    expect(fromBulkJob({ ...job, status: 'running' })).toBeNull();
  });
});

describe('FreshnessMonitor', () => {
  let get: ReturnType<typeof vi.fn>;
  let announce: ReturnType<typeof vi.spyOn>;

  function setup(): FreshnessMonitor {
    vi.useFakeTimers();
    get = vi.fn();
    TestBed.configureTestingModule({
      providers: [FreshnessMonitor, { provide: SearchFreshnessApi, useValue: { get } }],
    });
    announce = vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    return TestBed.inject(FreshnessMonitor);
  }

  afterEach(() => vi.useRealTimers());

  const served = (state: string, extra: Record<string, unknown> = {}) =>
    toServedFreshness({
      state,
      asOf: new Date().toISOString(),
      servedGeneration: '18432',
      ...extra,
    });

  it('polls every 5 s while the index is not current and stops once it is', async () => {
    const monitor = setup();
    monitor.observe(served('current'));
    await vi.advanceTimersByTimeAsync(20_000);
    expect(get).not.toHaveBeenCalled();

    monitor.observe(served('updating', { pendingChanges: 85, lagSeconds: 12 }));
    get
      .mockResolvedValueOnce(index())
      .mockResolvedValueOnce(index({ state: 'current', pendingChanges: 0, lagSeconds: 0 }));
    await vi.advanceTimersByTimeAsync(4_999);
    expect(get).toHaveBeenCalledTimes(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(get).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(get).toHaveBeenCalledTimes(2);
    expect(monitor.index()?.state).toBe('current');
    await vi.advanceTimersByTimeAsync(30_000);
    expect(get).toHaveBeenCalledTimes(2);
  });

  it('keeps polling while a job of the reviewer’s is catching up, and gives up on 404', async () => {
    const monitor = setup();
    monitor.observe(served('current'));
    get.mockResolvedValue(index({ state: 'current', pendingChanges: 0, lagSeconds: 0 }));
    monitor.follow(true);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(get).toHaveBeenCalledTimes(3);
    monitor.follow(false);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(get).toHaveBeenCalledTimes(3); // the poll already scheduled sees nothing to follow

    get.mockRejectedValue(new HttpErrorResponse({ status: 404 }));
    monitor.observe(served('updating'));
    await vi.advanceTimersByTimeAsync(30_000);
    expect(get).toHaveBeenCalledTimes(4);
  });

  it('announces state changes politely, at most once per 30 s (the latest state wins)', async () => {
    const monitor = setup();
    monitor.observe(served('current'));
    expect(announce).not.toHaveBeenCalled(); // the first state is shown, not announced
    monitor.observe(served('updating'));
    expect(announce).toHaveBeenCalledTimes(1);
    expect(announce).toHaveBeenLastCalledWith(
      'Search index is updating. Counts may not include recent changes.',
      'polite',
    );
    get.mockResolvedValue(index({ state: 'delayed', lagSeconds: 150 }));
    await vi.advanceTimersByTimeAsync(5_000); // → delayed
    monitor.observe(served('current')); // → current, 5 s after the last announcement
    expect(announce).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(24_999);
    expect(announce).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(announce).toHaveBeenCalledTimes(2);
    expect(announce).toHaveBeenLastCalledWith(
      'Search index is current. New searches include every saved change.',
      'polite',
    );
  });
});

describe('Documents freshness pill, footnote and job banner', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let freshness: Record<string, unknown>;
  let jobs: unknown[];
  let changed: unknown[];

  async function setup(
    options: { permissions?: string[]; served?: Record<string, unknown>; jobs?: unknown[] } = {},
  ): Promise<void> {
    freshness = {
      state: 'updating',
      indexedThroughGeneration: '18432',
      latestGeneration: '18517',
      pendingChanges: 85,
      lagSeconds: 12,
      asOf: '2026-10-04T10:42:05Z',
    };
    jobs = options.jobs ?? [];
    changed = [];
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: options.permissions ?? [PERMISSIONS.documentView, PERMISSIONS.searchExecute],
        },
      })
      .on('GET', `${WS}/fields`, { body: { items: [], nextCursor: null } })
      .on('POST', `${WS}/searches`, () => ({
        body: fakePage(
          {
            total: 250,
            pageSize: 100,
            current: false,
            freshness: options.served ?? {
              state: 'updating',
              servedGeneration: '18400',
              indexedThroughGeneration: '18400',
              pendingChanges: 85,
              lagSeconds: 12,
            },
          },
          1,
        ),
      }))
      .on('GET', `${WS}/search-freshness`, () => ({ body: freshness }))
      .on('GET', `${WS}/jobs`, (req) =>
        req.params.has('updatedSince')
          ? { body: { items: changed, nextCursor: null } }
          : { body: { items: jobs, nextCursor: null } },
      );
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: FRESHNESS_POLL_MS, useValue: 10 },
        { provide: JOB_POLL_MS, useValue: 10 },
      ],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
    await TestBed.inject(CommandRegistry).ready();
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';
  const pill = () => root().querySelector('opp-freshness-status')!;
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.replace(/\s+/g, ' ').trim() === name,
    );

  it('stamps the results, says what counts may miss and follows the index until it is current', async () => {
    await setup();
    expect(pill().textContent).toContain('Updating · ≈ 85 changes pending · ~12 s behind');
    expect(pill().querySelector('.badge--info opp-icon')).not.toBeNull(); // icon and text, not colour alone
    expect(text()).toContain('≈ 250 documents · Results as of 10:42');
    expect(text()).toContain('Counts may not include 85 recent changes.');
    // Reviewers never see generation numbers (Q-10).
    expect(text()).not.toMatch(/18,?4\d\d/);
    expect(pill().querySelector('button')).toBeNull();
    await expectNoAxeViolations(root());

    freshness = {
      ...freshness,
      state: 'current',
      pendingChanges: 0,
      lagSeconds: 0,
      indexedThroughGeneration: '18517',
    };
    await settle(20);
    expect(pill().textContent?.trim()).toBe('Search index: Current');
    // The served results are older than the index now: offer to run the search again.
    const include = button('Include recent changes')!;
    expect(include).toBeDefined();
    const polls = api.urls('GET').filter((u) => u.endsWith('/search-freshness')).length;
    await settle(30);
    expect(api.urls('GET').filter((u) => u.endsWith('/search-freshness')).length).toBe(polls);
    include.click();
    await settle();
    expect(api.requests.filter((r) => r.method === 'POST').length).toBe(2);
  }, 30_000); // axe over the Documents page is slow in jsdom on a loaded machine

  it('shows admins the raw generations in a detail popover', async () => {
    const finished = jobSummary({
      jobId: 'job-9',
      type: 'bulkCoding',
      status: 'completed',
      createdBy: { userId: 'u-1', displayName: 'Alex' },
      updatedAt: new Date().toISOString(),
      searchable: { done: 63, total: 100, state: 'catchingUp', jobGeneration: '18517' },
    });
    await setup({
      permissions: [PERMISSIONS.documentView, PERMISSIONS.searchExecute, 'Job.ViewAll'],
      jobs: [finished],
    });
    const toggle = pill().querySelector('button')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle();
    const panel = root().querySelector('[role="group"][aria-label="Search index details"]')!;
    expect(panel.textContent?.replace(/\s+/g, ' ')).toContain(
      'Index current through generation 18,432 / Job generation 18,517 (Mass Edit)',
    );
    expect(panel.textContent).toContain('Generation 18,400'); // these results
    expect(panel.textContent).toContain('12 s');
    await expectNoAxeViolations(root());
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();
    expect(root().querySelector('[aria-label="Search index details"]')).toBeNull();
    expect(document.activeElement).toBe(toggle);
  }, 30_000); // axe over the Documents page is slow in jsdom on a loaded machine

  it('shows the banner while the reviewer’s job is saved but not searchable, and clears it after catch-up', async () => {
    const job = jobSummary({
      jobId: 'job-9',
      type: 'bulkCoding',
      status: 'completed',
      createdBy: { userId: 'u-1', displayName: 'Alex' },
      updatedAt: '2026-10-04T10:00:00Z',
      searchable: { done: 63, total: 100, state: 'catchingUp' },
    });
    await setup({ jobs: [job] });
    expect(text()).toContain('Mass Edit saved · Search index updating… (63%)');
    const link = [...root().querySelectorAll('a')].find(
      (a) => a.textContent?.trim() === 'View job',
    )!;
    expect(link.getAttribute('href')).toBe('/w/ws-1/jobs/job-9');

    changed = [
      {
        ...job,
        updatedAt: '2026-10-04T10:01:00Z',
        searchable: { done: 100, total: 100, state: 'current' },
      },
    ];
    await settle(20);
    expect(text()).not.toContain('Search index updating…');
    // Appearing and clearing are announced politely (a burst is spaced out by the announcer).
    await vi.waitFor(
      () =>
        expect(TestBed.inject(LiveAnnouncer).announce).toHaveBeenCalledWith(
          'Mass Edit is now searchable.',
          'polite',
        ),
      { timeout: 3000 },
    );
  });
});
