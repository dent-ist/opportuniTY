import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { JOB_POLL_MS } from '../../core/jobs/job-feed';
import { jobDetail, jobSummary } from '../../core/jobs/job-fixtures.testing';
import type { JobDetail } from '../../core/jobs/job-model';
import { PERMISSIONS } from '../../core/workspace/sections';
import { ToastService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';

const WS = '/api/v1/workspaces/ws-1';

const running = jobDetail({
  jobId: 'job-1',
  name: 'VOL001.dat',
  createdBy: { userId: 'u-1', displayName: 'Alex' },
  correlationId: 'corr-1',
  snapshotId: 'snap-1',
});
const failedExport = jobDetail({
  jobId: 'job-2',
  type: 'export',
  name: 'Export A',
  status: 'completedWithErrors',
  createdBy: { userId: 'u-1', displayName: 'Alex' },
  createdAt: '2026-10-04T08:00:00Z',
  updatedAt: '2026-10-04T08:10:00Z',
  completedAt: '2026-10-04T08:10:00Z',
  committed: { done: 10, total: 10 },
  searchable: { done: 0, total: 0, state: 'notApplicable' },
  errorCount: 3,
  link: null,
  chunks: { pending: 0, running: 0, done: 7, failed: 2, deadLettered: 1, cancelled: 0 },
  attempts: 4,
  lastError: 'Object storage did not answer in time.',
  etaSeconds: null,
});
const othersJob = jobSummary({
  jobId: 'job-3',
  type: 'reindex',
  name: 'Search index rebuild',
  createdBy: { userId: 'u-2', displayName: 'Sam' },
});

describe('Jobs section (E06-T07)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let detail: Map<string, JobDetail>;
  let changed: JobDetail[];

  async function setup(permissions: string[] = [PERMISSIONS.documentView]): Promise<void> {
    detail = new Map([
      ['job-1', running],
      ['job-2', failedExport],
    ]);
    changed = [];
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/jobs`, (req) => {
        if (req.params.has('updatedSince')) return { body: { items: changed, nextCursor: null } };
        const all = req.params.get('createdBy') === 'all';
        const items = [running, failedExport, ...(all ? [othersJob] : [])].filter(
          (j) => !req.params.get('status') || j.status === req.params.get('status'),
        );
        return { body: { items, nextCursor: null } };
      })
      .on('GET', `${WS}/jobs/job-1`, () => ({ body: detail.get('job-1') }))
      .on('GET', `${WS}/jobs/job-2`, () => ({ body: detail.get('job-2') }))
      .on('GET', `${WS}/jobs/job-2/failures`, {
        body: {
          items: [
            {
              kind: 'chunk',
              id: 'chunk-8',
              attempts: 3,
              error: 'Timeout',
              failedAt: '2026-10-04T08:09:00Z',
            },
            {
              kind: 'deadLetter',
              id: 'chunk-10',
              attempts: 5,
              error: 'Unreadable',
              failedAt: '2026-10-04T08:10:00Z',
            },
          ],
          nextCursor: null,
        },
      })
      .on('POST', `${WS}/jobs/job-2/retry-failed`, { status: 202, body: {} })
      .on('POST', `${WS}/jobs/job-1/cancel`, { status: 202, body: {} });
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: JOB_POLL_MS, useValue: 10 },
      ],
    });
    localStorage.clear();
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = () => root().textContent?.replace(/\s+/g, ' ') ?? '';
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.replace(/\s+/g, ' ').trim() === name,
    );
  const requests = (method: string, url: string) =>
    api.requests.filter((r) => r.method === method && r.url === url);
  const listRequests = () =>
    requests('GET', `${WS}/jobs`).filter((r) => !r.params.has('updatedSince'));
  const lastList = (): HttpRequest<unknown> => listRequests().at(-1)!;

  it('lists the caller’s jobs with type, status, both phases and links; no creator filter without Job.ViewAll', async () => {
    await setup();
    await go('/w/ws-1/jobs');
    expect(lastList().params.get('createdBy')).toBe('me');
    const rows = [...root().querySelectorAll('tbody tr')];
    expect(rows.map((r) => r.querySelector('th')!.textContent!.trim())).toEqual([
      'VOL001.dat',
      'Export A',
    ]);
    expect(rows[0].textContent).toContain('Import');
    expect(rows[1].textContent).toContain('Completed with errors');
    expect(rows[1].textContent).toContain('Not needed');
    const saved = rows[0].querySelector('[role="progressbar"]')!;
    expect(saved.getAttribute('aria-label')).toBe('Saved: VOL001.dat');
    expect(saved.getAttribute('aria-valuetext')).toBe('40% saved, 40 of 100');
    expect(rows[0].querySelector('th a')!.getAttribute('href')).toBe('/w/ws-1/jobs/job-1');
    const own = [...rows[0].querySelectorAll('a')].find((a) => a.textContent!.includes('Open'))!;
    expect(own.getAttribute('href')).toBe('/w/ws-1/imports/imp-1');
    expect(own.textContent!.replace(/\s+/g, ' ').trim()).toBe('Open import VOL001.dat');
    expect(text()).not.toContain('Started by');
    await expectNoAxeViolations(root());
  });

  it('filters by type, status, creator and date through the URL', async () => {
    await setup([PERMISSIONS.documentView, 'Job.ViewAll']);
    await go('/w/ws-1/jobs');
    expect(lastList().params.get('createdBy')).toBe('all');
    expect(text()).toContain('Search index rebuild');
    expect(text()).toContain('Started by');

    const selects = [...root().querySelectorAll('select')];
    const status = selects.find((s) => s.closest('opp-select')!.textContent!.includes('Status'))!;
    status.value = 'completedWithErrors';
    status.dispatchEvent(new Event('change'));
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/jobs?status=completedWithErrors');
    expect(lastList().params.get('status')).toBe('completedWithErrors');
    expect([...root().querySelectorAll('tbody th')].map((t) => t.textContent!.trim())).toEqual([
      'Export A',
    ]);

    const from = root().querySelector<HTMLInputElement>('input[type="date"]')!;
    from.value = '2026-10-04';
    from.dispatchEvent(new Event('input'));
    await settle();
    expect(lastList().params.get('from')).toBe(new Date(2026, 9, 4).toISOString());

    const creator = selects.find((s) =>
      s.closest('opp-select')!.textContent!.includes('Started by'),
    )!;
    creator.value = 'me';
    creator.dispatchEvent(new Event('change'));
    await settle();
    expect(lastList().params.get('createdBy')).toBe('me');

    button('Clear filters')!.click();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/jobs');
    expect(lastList().params.keys()).toEqual(['createdBy', 'limit']);
  });

  it('shows the job: two progress bars with value text, time left, counts and copyable ids', async () => {
    await setup();
    await go('/w/ws-1/jobs/job-1');
    expect(root().querySelector('h1')!.textContent).toBe('VOL001.dat');
    const bars = [...root().querySelectorAll('[role="progressbar"]')];
    expect(bars.map((b) => b.getAttribute('aria-label'))).toEqual(['Saved', 'Searchable']);
    expect(bars[0].getAttribute('aria-valuenow')).toBe('40');
    expect(bars[0].getAttribute('aria-valuetext')).toBe(
      '40% saved, 40 of 100, About 3 minutes left',
    );
    expect(bars[1].getAttribute('aria-valuetext')).toBe('10% searchable, 10 of 100');
    expect(text()).toContain('About 3 minutes left');
    expect(text()).toContain('Updating the search index…');
    expect(text()).toContain('corr-1');
    expect(text()).toContain('snap-1');
    // The owner may cancel; retry needs permission and failed chunks.
    expect(button('Cancel job')).toBeDefined();
    expect(button('Retry failed chunks')).toBeUndefined();

    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    root().querySelector<HTMLButtonElement>('button[aria-label="Copy correlation ID"]')!.click();
    await settle();
    expect(writeText).toHaveBeenCalledWith('corr-1');
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe('Correlation ID copied.');
    await expectNoAxeViolations(root());
  });

  it('follows the job live and announces the status change', async () => {
    await setup();
    await go('/w/ws-1/jobs/job-1');
    expect(root().querySelector('opp-status-pill')!.textContent).toContain('Running');
    const done = {
      ...running,
      status: 'completed',
      committed: { done: 100, total: 100 },
      searchable: { done: 100, total: 100, state: 'current' as const },
      updatedAt: '2026-10-04T09:30:00Z',
      completedAt: '2026-10-04T09:30:00Z',
    };
    detail.set('job-1', done);
    changed = [done];
    for (let i = 0; i < 50 && !text().includes('Completed'); i++) await settle(5);
    expect(root().querySelector('opp-status-pill')!.textContent).toContain('Completed');
    expect(button('Cancel job')).toBeUndefined();
    expect(text()).toContain('Updates every');
    await settle(40); // The live announcer writes after a short delay.
    const live = document.querySelector('.cdk-live-announcer-element');
    expect(live?.textContent).toContain('Job status: Completed');
  });

  it('lists failed chunks; Retry failed chunks only with permission, idempotent', async () => {
    await setup();
    await go('/w/ws-1/jobs/job-2');
    expect(text()).toContain('Failed chunks');
    expect(text()).toContain('chunk-10');
    expect(text()).toContain('Object storage did not answer in time.');
    expect(button('Retry failed chunks')).toBeUndefined();
    expect(text()).toContain('A workspace admin can retry failed chunks.');
    // Finished: nothing to cancel.
    expect(button('Cancel job')).toBeUndefined();

    TestBed.resetTestingModule();
    await setup([PERMISSIONS.documentView, 'Job.Replay']);
    await go('/w/ws-1/jobs/job-2');
    button('Retry failed chunks')!.click();
    await settle();
    const [retry] = requests('POST', `${WS}/jobs/job-2/retry-failed`);
    expect(retry.headers.get('Idempotency-Key')).toBeTruthy();
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe(
      'Retrying 3 failed chunks of “Export A”.',
    );
    await expectNoAxeViolations(root());
  });

  it('cancels after confirmation', async () => {
    await setup();
    await go('/w/ws-1/jobs/job-1');
    button('Cancel job')!.click();
    let dialog: HTMLElement | null = null;
    for (let i = 0; i < 100 && !dialog; i++) {
      await settle(5);
      dialog = document.querySelector<HTMLElement>('[role="alertdialog"]');
    }
    expect(dialog!.textContent).toContain('Cancel this job?');
    const confirm = [...dialog!.querySelectorAll('button')].find(
      (b) => b.textContent!.trim() === 'Cancel job',
    )!;
    confirm.click();
    await settle(5);
    expect(requests('POST', `${WS}/jobs/job-1/cancel`).length).toBe(1);
  });

  it('shows the running count and recent jobs in the header tray, and a lasting notice when a job fails', async () => {
    await setup();
    await go('/w/ws-1/jobs');
    const tray = (harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'opp-job-tray button',
    )!;
    expect(tray.getAttribute('aria-label')).toBe('Jobs, 1 running');
    tray.click();
    await settle();
    const items = [...document.querySelectorAll('[role="menuitem"]')].map((i) =>
      i.textContent!.replace(/\s+/g, ' ').trim(),
    );
    expect(items).toEqual([
      'Import · VOL001.dat: Running · Saved 40% · Searchable 10%',
      'Export · Export A: Completed with errors · Saved 100%',
      'All jobs',
    ]);
    await expectNoAxeViolations(document.querySelector('[role="menu"]')!);

    changed = [
      { ...running, status: 'failed', updatedAt: new Date(Date.now() + 1000).toISOString() },
    ];
    const toasts = TestBed.inject(ToastService);
    for (let i = 0; i < 50 && !toasts.toasts().length; i++) await settle(5);
    expect(toasts.toasts()[0]).toMatchObject({
      message: 'Import “VOL001.dat” failed.',
      tone: 'error',
      durationMs: null,
    });
    expect(tray.getAttribute('aria-label')).toBe('Jobs, none running');
  });

  it('shows the API’s refusal for another user’s job', async () => {
    await setup();
    api.on('GET', `${WS}/jobs/job-3`, { status: 403, body: { title: 'Forbidden', status: 403 } });
    await go('/w/ws-1/jobs/job-3');
    expect(root().querySelector('[role="alert"]')).not.toBeNull();
  });
});
