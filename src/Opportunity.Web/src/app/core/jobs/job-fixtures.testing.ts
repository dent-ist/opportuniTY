import type { JobDetail, JobSummary } from './job-model';

/** A running import job; tests override what they need. */
export function jobSummary(overrides: Partial<JobSummary> = {}): JobSummary {
  return {
    jobId: 'job-1',
    type: 'import',
    name: 'VOL001.dat',
    status: 'running',
    createdBy: { userId: 'u-1', displayName: 'Alex' },
    createdAt: '2026-10-04T09:00:00Z',
    updatedAt: '2026-10-04T09:01:00Z',
    completedAt: null,
    committed: { done: 40, total: 100 },
    searchable: { done: 10, total: 100, state: 'catchingUp' },
    errorCount: 0,
    correlationId: 'corr-1',
    snapshotId: null,
    link: 'imports/imp-1',
    ...overrides,
  };
}

export function jobDetail(overrides: Partial<JobDetail> = {}): JobDetail {
  return {
    ...jobSummary(),
    chunks: { pending: 5, running: 1, done: 4, failed: 0, deadLettered: 0, cancelled: 0 },
    attempts: 1,
    lastError: null,
    etaSeconds: 150,
    ...overrides,
  };
}
