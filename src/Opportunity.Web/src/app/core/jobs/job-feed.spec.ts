import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ToastService } from '../../ui';
import { ApiError } from '../api/problem-details';
import { SessionService } from '../session/session';
import { WorkspaceContext } from '../workspace/workspace-context';
import { jobDetail, jobSummary } from './job-fixtures.testing';
import { ActiveJobFeed, JOB_POLL_MS, JOB_STREAM_RETRY_MS, JobFeed } from './job-feed';
import type { JobDetail, JobEvent, JobSummary, Page } from './job-model';
import { JobStream, JobStreamHandlers, JobsApi } from './jobs-api';

class FakeJobsApi extends JobsApi {
  recent: JobSummary[] = [];
  changed: JobSummary[] = [];
  listError: unknown = null;
  readonly listCalls: string[] = [];
  readonly sinceCalls: string[] = [];
  readonly details = new Map<string, JobDetail>();
  stream = true;
  handlers: JobStreamHandlers | null = null;
  streams = 0;
  closed = 0;

  async list(filter: { createdBy: string }): Promise<Page<JobSummary>> {
    this.listCalls.push(filter.createdBy);
    if (this.listError) throw this.listError;
    return { items: this.recent, nextCursor: null };
  }
  async updatedSince(since: string): Promise<Page<JobSummary>> {
    this.sinceCalls.push(since);
    return { items: this.changed, nextCursor: null };
  }
  async get(jobId: string): Promise<JobDetail> {
    const d = this.details.get(jobId);
    if (!d) throw new ApiError(404, { title: 'Not found' });
    return d;
  }
  async failures(): Promise<Page<never>> {
    return { items: [], nextCursor: null };
  }
  async cancel(): Promise<void> {}
  async retryFailed(): Promise<void> {}
  events(handlers: JobStreamHandlers): JobStream | null {
    if (!this.stream) return null;
    this.handlers = handlers;
    this.streams++;
    return { close: () => this.closed++ };
  }
}

const event = (job: JobSummary, patch: Partial<JobEvent>): JobEvent => ({
  jobId: job.jobId,
  status: job.status,
  committed: job.committed,
  searchable: job.searchable,
  updatedAt: new Date(Date.now() + 1000).toISOString(),
  ...patch,
});

describe('JobFeed (E06-T07)', () => {
  let api: FakeJobsApi;
  let feed: JobFeed;

  async function setup(
    options: { permissions?: string[]; pollMs?: number; configure?: () => void } = {},
  ) {
    api = new FakeJobsApi();
    options.configure?.();
    const permissions = options.permissions ?? [];
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsApi, useValue: api },
        {
          provide: WorkspaceContext,
          useValue: { workspaceId: 'ws-1', can: (p: string) => permissions.includes(p) },
        },
        { provide: SessionService, useValue: { principal: signal({ userId: 'u-1' }) } },
        { provide: JOB_POLL_MS, useValue: options.pollMs ?? 20 },
        { provide: JOB_STREAM_RETRY_MS, useValue: 60 },
        JobFeed,
      ],
    });
    feed = TestBed.inject(JobFeed);
    await settle();
  }

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));
  const toasts = () => TestBed.inject(ToastService).toasts();

  it('reads the recent jobs, registers for the tray and follows the event stream', async () => {
    await setup({
      configure: () =>
        (api.recent = [
          jobSummary(),
          jobSummary({ jobId: 'job-2', status: 'completed', updatedAt: '2026-10-04T08:00:00Z' }),
        ]),
    });
    expect(TestBed.inject(ActiveJobFeed).current()).toBe(feed);
    expect(api.listCalls).toEqual(['me']);
    expect(feed.running()).toBe(1);
    expect(feed.recent().map((j) => j.jobId)).toEqual(['job-1', 'job-2']);
    expect(api.streams).toBe(1);
    // An already finished job is not announced as news.
    expect(toasts()).toEqual([]);

    api.handlers!.open();
    expect(feed.mode()).toBe('live');
    await settle();
    expect(api.sinceCalls.length).toBe(0); // the list was just read
    await settle(30);
    expect(api.sinceCalls.length).toBe(1); // catch-up after (re)connecting

    api.handlers!.event(event(jobSummary(), { committed: { done: 90, total: 100 } }));
    expect(feed.jobs().get('job-1')?.committed.done).toBe(90);

    TestBed.resetTestingModule();
    expect(api.closed).toBe(1);
  });

  it('catches up after reconnecting at most once per polling interval', async () => {
    await setup({ pollMs: 10_000, configure: () => (api.recent = [jobSummary()]) });
    // A connection that keeps dropping and reopening reads no more often than polling would.
    for (let i = 0; i < 5; i++) {
      api.handlers!.open();
      api.handlers!.error(false);
    }
    api.handlers!.open();
    await settle(30);
    expect(api.sinceCalls).toEqual([]);
    expect(feed.mode()).toBe('live');
  });

  it('notifies when a job finishes: success fades, failure persists with a link to the job', async () => {
    const running = jobSummary();
    const other = jobSummary({ jobId: 'job-2', name: 'Export A', type: 'export' });
    await setup({ configure: () => (api.recent = [running, other]) });
    api.handlers!.event(event(running, { status: 'completed' }));
    api.handlers!.event(event(other, { status: 'failed' }));
    const [done, failed] = toasts();
    expect(done).toMatchObject({
      message: 'Import “VOL001.dat” completed. Saved; becoming searchable.',
      tone: 'success',
    });
    expect(done.durationMs).not.toBeNull();
    expect(failed).toMatchObject({ message: 'Export “Export A” failed.', tone: 'error' });
    expect(failed.durationMs).toBeNull();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    failed.action!.run();
    expect(navigate).toHaveBeenCalledWith(['/w', 'ws-1', 'jobs', 'job-2']);
    // The same state again is not news.
    api.handlers!.event(event(running, { status: 'completed' }));
    expect(toasts().length).toBe(2);
  });

  it('fetches a job first seen in the stream, and notifies only about the caller’s own jobs', async () => {
    await setup();
    const now = new Date(Date.now() + 1000).toISOString();
    api.details.set(
      'job-9',
      jobDetail({ jobId: 'job-9', name: 'Mine', status: 'failed', updatedAt: now }),
    );
    api.details.set(
      'job-8',
      jobDetail({
        jobId: 'job-8',
        name: 'Theirs',
        status: 'failed',
        updatedAt: now,
        createdBy: { userId: 'u-2', displayName: 'Sam' },
      }),
    );
    api.handlers!.event(event(jobSummary({ jobId: 'job-9' }), { status: 'failed' }));
    api.handlers!.event(event(jobSummary({ jobId: 'job-8' }), { status: 'failed' }));
    await settle();
    expect(feed.jobs().has('job-9')).toBe(true);
    expect(toasts().map((t) => t.message)).toEqual(['Import “Mine” failed.']);
  });

  it('follows every job with Job.ViewAll', async () => {
    await setup({
      permissions: ['Job.ViewAll'],
      configure: () =>
        (api.recent = [jobSummary({ createdBy: { userId: 'u-2', displayName: 'Sam' } })]),
    });
    expect(feed.scope).toBe('all');
    expect(api.listCalls).toEqual(['all']);
    api.handlers!.event(event(jobSummary(), { status: 'completedWithErrors' }));
    expect(toasts()[0]).toMatchObject({ tone: 'warning' });
    expect(toasts()[0].durationMs).toBeNull();
  });

  it('leaves the notification to a feature that reports the outcome itself', async () => {
    await setup({ configure: () => (api.recent = [jobSummary()]) });
    feed.reportedElsewhere('job-1');
    api.handlers!.event(event(jobSummary(), { status: 'completed' }));
    expect(feed.jobs().get('job-1')?.status).toBe('completed');
    expect(toasts()).toEqual([]);
  });

  it('polls updatedSince when the browser cannot stream', async () => {
    await setup({
      configure: () => {
        api.stream = false;
        api.recent = [jobSummary()];
      },
    });
    expect(feed.mode()).toBe('polling');
    api.changed = [jobSummary({ status: 'completed', updatedAt: '2026-10-04T09:05:00Z' })];
    await settle(70);
    expect(api.sinceCalls.length).toBeGreaterThanOrEqual(2);
    expect(api.sinceCalls[0]).toBe('2026-10-04T09:01:00Z');
    expect(api.sinceCalls.at(-1)).toBe('2026-10-04T09:05:00Z');
    expect(feed.running()).toBe(0);
    expect(toasts().length).toBe(1);
  });

  it('falls back to polling when the stream fails for good, and tries the stream again later', async () => {
    await setup({ configure: () => (api.recent = [jobSummary()]) });
    api.handlers!.open();
    await settle();
    const polls = api.sinceCalls.length;
    // A dropped connection the browser re-opens by itself does not switch to polling at once.
    api.handlers!.error(false);
    api.handlers!.open();
    await settle(40);
    expect(feed.mode()).toBe('live');
    api.handlers!.error(true);
    expect(feed.mode()).toBe('polling');
    expect(api.closed).toBe(1);
    await settle(45);
    expect(api.sinceCalls.length).toBeGreaterThan(polls + 1);
    await settle(40);
    expect(api.streams).toBe(2);
  });

  it('stops when the jobs API is not available to the caller', async () => {
    await setup({ configure: () => (api.listError = new ApiError(404, { title: 'Not found' })) });
    expect(feed.mode()).toBe('unavailable');
    expect(api.streams).toBe(0);
    await settle(50);
    expect(api.sinceCalls).toEqual([]);
  });
});
