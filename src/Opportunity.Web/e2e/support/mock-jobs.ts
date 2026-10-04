import type { Route } from '@playwright/test';

/**
 * Job operations (wave-9 contract "Job operations", #61) for the e2e mock: `GET …/jobs` (filters, `updatedSince`),
 * `GET …/jobs/{id}`, `GET …/jobs/{id}/failures`, `POST …/cancel` and `…/retry-failed`, and the live stream
 * `GET …/job-events`. A route handler cannot hold a response open, so the stream answers the events since the
 * browser's `Last-Event-ID` with `retry: 1000`; the browser reconnects every second, which is how a long-lived
 * stream behaves to the page apart from latency. `stream: false` answers 404 instead, so the page polls.
 */
export interface JobsMockOptions {
  /** The signed-in user. */
  userId: string;
  /** `Job.ViewAll`: every user's jobs are listed and streamed; otherwise only the caller's own. */
  viewAll: boolean;
  /** Serve `GET …/job-events`; false answers 404 so the polling fallback is used. */
  stream: boolean;
  /** Jobs other mocks own (an import started in the wizard), in any shape `GET …/jobs/{id}` may return. */
  lookup?: (jobId: string) => Record<string, unknown> | undefined;
}

export interface MockJobFailure {
  kind: string;
  id: string;
  attempts: number;
  error: string;
  failedAt: string;
}

export interface MockJob {
  jobId: string;
  type: string;
  name: string;
  status: string;
  createdBy: { userId: string; displayName: string };
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  committed: { done: number; total: number };
  searchable: { done: number; total: number; state: string };
  errorCount: number;
  correlationId: string;
  snapshotId: string | null;
  link: string | null;
  chunks: {
    pending: number;
    running: number;
    done: number;
    failed: number;
    deadLettered: number;
    cancelled: number;
  };
  attempts: number;
  lastError: string | null;
  etaSeconds: number | null;
  failures: MockJobFailure[];
}

const ALEX = { userId: 'user-1', displayName: 'Alex Reviewer' };
const SAM = { userId: 'user-2', displayName: 'Sam Admin' };

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

function seed(): MockJob[] {
  const base = {
    completedAt: null,
    errorCount: 0,
    snapshotId: null,
    link: null,
    attempts: 1,
    lastError: null,
    etaSeconds: null,
    failures: [],
  };
  return [
    {
      ...base,
      jobId: 'job-bulk-7',
      type: 'bulkCoding',
      name: 'Mass Edit – Responsiveness',
      status: 'running',
      createdBy: ALEX,
      createdAt: minutesAgo(3),
      updatedAt: minutesAgo(0.1),
      committed: { done: 400, total: 1000 },
      searchable: { done: 300, total: 1000, state: 'catchingUp' },
      correlationId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      snapshotId: 'snapshot-7',
      chunks: { pending: 5, running: 1, done: 4, failed: 0, deadLettered: 0, cancelled: 0 },
      etaSeconds: 150,
    },
    {
      ...base,
      jobId: 'job-exp-3',
      type: 'export',
      name: 'Export ACME_EXP003',
      status: 'completedWithErrors',
      createdBy: ALEX,
      createdAt: minutesAgo(40),
      updatedAt: minutesAgo(30),
      completedAt: minutesAgo(30),
      committed: { done: 10, total: 10 },
      searchable: { done: 0, total: 0, state: 'notApplicable' },
      errorCount: 3,
      correlationId: '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01',
      snapshotId: 'snapshot-3',
      chunks: { pending: 0, running: 0, done: 7, failed: 2, deadLettered: 1, cancelled: 0 },
      attempts: 4,
      lastError: 'Object storage did not answer in time.',
      failures: [
        {
          kind: 'chunk',
          id: 'chunk-8',
          attempts: 3,
          error: 'Object storage did not answer in time.',
          failedAt: minutesAgo(31),
        },
        {
          kind: 'chunk',
          id: 'chunk-9',
          attempts: 3,
          error: 'Object storage did not answer in time.',
          failedAt: minutesAgo(31),
        },
        {
          kind: 'deadLetter',
          id: 'chunk-10',
          attempts: 5,
          error: 'Native file is unreadable.',
          failedAt: minutesAgo(30),
        },
      ],
    },
    {
      ...base,
      jobId: 'job-imp-1',
      type: 'import',
      name: 'VOL001.dat 2026-10-01',
      status: 'completed',
      createdBy: ALEX,
      createdAt: minutesAgo(60 * 24),
      updatedAt: minutesAgo(60 * 24 - 2),
      completedAt: minutesAgo(60 * 24 - 2),
      committed: { done: 2, total: 2 },
      searchable: { done: 2, total: 2, state: 'current' },
      correlationId: '00-5c2a1f7e9b8d4c6e8f0a1b2c3d4e5f60-1a2b3c4d5e6f7081-01',
      link: 'imports/imp-1',
      chunks: { pending: 0, running: 0, done: 2, failed: 0, deadLettered: 0, cancelled: 0 },
    },
    {
      ...base,
      jobId: 'job-idx-2',
      type: 'reindex',
      name: 'Search index rebuild',
      status: 'running',
      createdBy: SAM,
      createdAt: minutesAgo(10),
      updatedAt: minutesAgo(0.2),
      committed: { done: 0, total: 0 },
      searchable: { done: 1200, total: 5000, state: 'catchingUp' },
      correlationId: '00-6d3b2a1f0e9d8c7b6a5f4e3d2c1b0a99-2b3c4d5e6f708192-01',
      chunks: { pending: 30, running: 2, done: 12, failed: 0, deadLettered: 0, cancelled: 0 },
      etaSeconds: 900,
    },
  ];
}

export class JobsMock {
  readonly cancels: string[] = [];
  readonly retries: { jobId: string; idempotencyKey: string | null }[] = [];
  /** Connections to `GET …/job-events` and polls of `GET …/jobs?updatedSince=`. */
  streamConnections = 0;
  readonly polls: string[] = [];
  private readonly jobs = new Map(seed().map((j) => [j.jobId, j]));
  private readonly events: { seq: number; job: MockJob }[] = [];
  private seq = 0;

  constructor(private readonly options: JobsMockOptions) {}

  /** A change on the "server": patches the job, stamps it and publishes it to the stream. */
  update(jobId: string, patch: Partial<MockJob>): MockJob {
    const job = { ...this.jobs.get(jobId)!, ...patch, updatedAt: new Date().toISOString() };
    if (['completed', 'completedWithErrors', 'failed', 'cancelled'].includes(job.status)) {
      job.completedAt ??= job.updatedAt;
      job.etaSeconds = null;
    }
    this.jobs.set(jobId, job);
    this.events.push({ seq: ++this.seq, job });
    return job;
  }

  /** Starts a new job (it appears in the list and the tray through the stream). */
  add(job: Partial<MockJob> & { jobId: string; type: string; name: string }): MockJob {
    const now = new Date().toISOString();
    const created: MockJob = {
      ...seed()[0],
      status: 'running',
      createdBy: ALEX,
      createdAt: now,
      updatedAt: now,
      snapshotId: null,
      link: null,
      ...job,
    };
    this.jobs.set(created.jobId, created);
    this.events.push({ seq: ++this.seq, job: created });
    return created;
  }

  get(jobId: string): MockJob | undefined {
    return this.jobs.get(jobId);
  }

  private visible(job: MockJob): boolean {
    return this.options.viewAll || job.createdBy.userId === this.options.userId;
  }

  /** Answers a job route, or returns undefined. */
  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const m = /^\/api\/v1\/workspaces\/[^/]+\/(jobs|job-events)(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(
      path,
    );
    if (!m) return undefined;
    const [, area, jobId, action] = m;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    const notFound = () =>
      route.fulfill({
        status: 404,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'about:blank', title: 'Not found', status: 404 }),
      });

    if (area === 'job-events' && method === 'GET' && !jobId) {
      if (!this.options.stream) return notFound();
      this.streamConnections++;
      const last = Number(route.request().headers()['last-event-id'] ?? NaN);
      const since = Number.isFinite(last) ? last : this.seq;
      const body = [
        'retry: 1000',
        `id: ${this.seq}`,
        ': connected',
        '',
        ...this.events
          .filter((e) => e.seq > since && this.visible(e.job))
          .flatMap((e) => [
            `id: ${e.seq}`,
            `data: ${JSON.stringify({
              jobId: e.job.jobId,
              status: e.job.status,
              committed: e.job.committed,
              searchable: e.job.searchable,
              updatedAt: e.job.updatedAt,
            })}`,
            '',
          ]),
        '',
      ].join('\n');
      return route.fulfill({
        status: 200,
        contentType: 'text/event-stream',
        headers: { 'Cache-Control': 'no-store' },
        body,
      });
    }
    if (area !== 'jobs') return undefined;

    if (!jobId && method === 'GET') {
      const p = url.searchParams;
      const updatedSince = p.get('updatedSince');
      if (updatedSince) this.polls.push(updatedSince);
      const createdBy = p.get('createdBy') === 'all' && this.options.viewAll ? 'all' : 'me';
      const items = [...this.jobs.values()]
        .filter((j) => this.visible(j))
        .filter((j) => createdBy === 'all' || j.createdBy.userId === this.options.userId)
        .filter((j) => !updatedSince || Date.parse(j.updatedAt) > Date.parse(updatedSince))
        .filter((j) => !p.get('type') || j.type === p.get('type'))
        .filter((j) => !p.get('status') || j.status === p.get('status'))
        .filter((j) => !p.get('from') || Date.parse(j.createdAt) >= Date.parse(p.get('from')!))
        .filter((j) => !p.get('to') || Date.parse(j.createdAt) <= Date.parse(p.get('to')!))
        .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
        .map(
          ({ chunks: _c, attempts: _a, lastError: _l, etaSeconds: _e, failures: _f, ...s }) => s,
        );
      return json({ items, nextCursor: null });
    }

    const job = jobId ? this.jobs.get(jobId) : undefined;
    if (!job) {
      const other = jobId && !action && method === 'GET' ? this.options.lookup?.(jobId) : undefined;
      return other ? json(other) : notFound();
    }
    if (!this.visible(job)) {
      // Q-59: a member who may not see another user's job gets 403 on its detail.
      return route.fulfill({
        status: 403,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'about:blank', title: 'Forbidden', status: 403 }),
      });
    }
    if (!action && method === 'GET') {
      const { failures: _f, ...detail } = job;
      return json(detail);
    }
    if (action === 'failures' && method === 'GET') {
      return json({ items: job.failures, nextCursor: null });
    }
    if (action === 'cancel' && method === 'POST') {
      this.cancels.push(job.jobId);
      this.update(job.jobId, { status: 'cancelling' });
      setTimeout(() => {
        const current = this.jobs.get(job.jobId)!;
        this.update(job.jobId, {
          status: 'cancelled',
          chunks: { ...current.chunks, cancelled: current.chunks.pending, pending: 0, running: 0 },
        });
      }, 300);
      return route.fulfill({ status: 202, json: { jobId: job.jobId } });
    }
    if (action === 'retry-failed' && method === 'POST') {
      this.retries.push({
        jobId: job.jobId,
        idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
      });
      const retried = job.chunks.failed + job.chunks.deadLettered;
      this.update(job.jobId, {
        status: 'running',
        completedAt: null,
        attempts: job.attempts + 1,
        chunks: { ...job.chunks, failed: 0, deadLettered: 0, pending: retried },
        committed: { done: job.committed.done - retried, total: job.committed.total },
        failures: [],
      });
      return route.fulfill({ status: 202, json: { jobId: job.jobId } });
    }
    return undefined;
  }
}
