import { jobSummary } from './job-fixtures.testing';
import {
  applyEvent,
  canCancel,
  canRetry,
  canViewAll,
  completionNotice,
  dayEnd,
  dayStart,
  etaText,
  failureKindLabel,
  filterParams,
  isSettled,
  matchesFilter,
  newer,
  phasesText,
  pillStatus,
  searchableText,
  statusLabel,
  toJobDetail,
  toJobEvent,
  toJobSummary,
  typeLabel,
} from './job-model';

const caller = (userId: string | null, permissions: string[] = []) => ({
  userId,
  can: (p: string) => permissions.includes(p),
});

describe('job model (E06-T07)', () => {
  it('names types and statuses in plain language', () => {
    expect(typeLabel('bulkCoding')).toBe('Mass Edit');
    expect(typeLabel('import')).toBe('Import');
    expect(typeLabel('reindexWorkspace')).toBe('Reindex workspace');
    expect(statusLabel('completedWithErrors')).toBe('Completed with errors');
    expect(statusLabel('created')).toBe('Queued');
    expect(pillStatus('failed')).toBe('failed');
    expect(pillStatus('somethingNew')).toBe('running');
  });

  it('names failure kinds, including recorded dead-lettered messages (ADR-010 §7.3)', () => {
    expect(failureKindLabel('chunk')).toBe('Chunk');
    expect(failureKindLabel('indexTask')).toBe('Index task');
    expect(failureKindLabel('deadLetter')).toBe('Dead-lettered message');
    expect(failureKindLabel('somethingNew')).toBe('somethingNew');
  });

  it('describes the two phases as Saved and Searchable', () => {
    expect(phasesText(jobSummary(), 'en-US')).toBe('Saved 40% · Searchable 10%');
    expect(
      phasesText(
        jobSummary({
          status: 'completed',
          committed: { done: 0, total: 0 },
          searchable: { done: 0, total: 0, state: 'notApplicable' },
        }),
        'en-US',
      ),
    ).toBe('Saved 100%');
    expect(
      phasesText(
        jobSummary({ status: 'completed', searchable: { done: 5, total: 5, state: 'current' } }),
        'en-US',
      ),
    ).toBe('Saved 40% · Searchable');
    expect(
      searchableText(jobSummary({ searchable: { done: 0, total: 0, state: 'pending' } })),
    ).toBe('After saving');
  });

  it('says how long is left', () => {
    expect(etaText(null)).toBeNull();
    expect(etaText(20)).toBe('Less than a minute left');
    expect(etaText(61)).toBe('About 2 minutes left');
    expect(etaText(60)).toBe('About 1 minute left');
    expect(etaText(3 * 3600)).toBe('About 3 hours left');
  });

  it('is settled only when nothing will change any more', () => {
    expect(isSettled(jobSummary())).toBe(false);
    expect(isSettled(jobSummary({ status: 'completed' }))).toBe(false);
    expect(
      isSettled(
        jobSummary({ status: 'completed', searchable: { done: 1, total: 1, state: 'current' } }),
      ),
    ).toBe(true);
    expect(isSettled(jobSummary({ status: 'failed' }))).toBe(true);
  });

  it('allows retry with Job.Replay only, and cancel to the owner or with Job.Manage', () => {
    expect(canRetry(caller('u-1'))).toBe(false);
    expect(canRetry(caller('u-1', ['Job.Replay']))).toBe(true);
    expect(canRetry(caller('u-1', ['Job.Manage']))).toBe(false);
    expect(canViewAll(caller('u-1', ['Job.ViewAll']))).toBe(true);
    const job = jobSummary();
    expect(canCancel(job, caller('u-1'))).toBe(true);
    expect(canCancel(job, caller('u-2'))).toBe(false);
    expect(canCancel(job, caller('u-2', ['Job.Manage']))).toBe(true);
    expect(canCancel(jobSummary({ status: 'completed' }), caller('u-1', ['Job.Manage']))).toBe(
      false,
    );
    expect(canCancel(jobSummary({ status: 'cancelling' }), caller('u-1'))).toBe(false);
  });

  it('applies live events in order and ignores stale ones', () => {
    const job = jobSummary();
    const event = {
      jobId: 'job-1',
      status: 'completed',
      committed: { done: 100, total: 100 },
      searchable: { done: 20, total: 100, state: 'catchingUp' as const },
      updatedAt: '2026-10-04T09:02:00Z',
    };
    expect(applyEvent(job, event)).toMatchObject({ status: 'completed', committed: { done: 100 } });
    expect(applyEvent(job, { ...event, updatedAt: '2026-10-04T09:00:30Z' })).toBeNull();
    const detail = toJobDetail({ ...job, chunks: { failed: 2 }, attempts: 3 });
    const merged = newer(detail, { ...job, status: 'failed', updatedAt: '2026-10-04T09:05:00Z' });
    expect(merged.status).toBe('failed');
    expect(merged.chunks.failed).toBe(2);
    expect(newer(detail, job)).toBe(detail);
  });

  it('builds the list query and matches new jobs against the filters', () => {
    const filter = {
      type: 'import',
      status: '',
      createdBy: 'me' as const,
      from: '2026-10-04',
      to: '',
    };
    expect(filterParams(filter)).toEqual({
      createdBy: 'me',
      type: 'import',
      from: dayStart('2026-10-04'),
    });
    expect(Date.parse(dayEnd('2026-10-04')!) - Date.parse(dayStart('2026-10-04')!)).toBe(
      24 * 3600_000 - 1,
    );
    expect(dayStart('not a day')).toBeNull();
    const job = jobSummary({ createdAt: new Date(2026, 9, 4, 12).toISOString() });
    expect(matchesFilter(job, filter, 'u-1')).toBe(true);
    expect(matchesFilter(job, filter, 'u-2')).toBe(false);
    expect(matchesFilter(job, { ...filter, type: 'export' }, 'u-1')).toBe(false);
    expect(matchesFilter(job, { ...filter, from: '2026-10-05' }, 'u-1')).toBe(false);
  });

  it('words completion notices; failures and errors are problems', () => {
    expect(completionNotice(jobSummary())).toBeNull();
    expect(completionNotice(jobSummary({ status: 'completed' }))).toEqual({
      message: 'Import “VOL001.dat” completed. Saved; becoming searchable.',
      tone: 'success',
    });
    expect(completionNotice(jobSummary({ status: 'failed' }))?.tone).toBe('error');
    expect(
      completionNotice(jobSummary({ status: 'completedWithErrors', errorCount: 1 }))?.message,
    ).toBe('Import “VOL001.dat” completed with 1 error.');
  });

  it('reads the contract tolerantly, and the M1 job resource too', () => {
    const summary = toJobSummary({
      jobId: 'j',
      type: 'export',
      name: 'E',
      status: 'running',
      createdBy: { userId: 'u', displayName: 'U' },
      committed: { done: '5', total: '10' },
      searchable: { done: 0, total: 0, state: 'weird' },
      errorCount: '2',
    });
    expect(summary.committed).toEqual({ done: 5, total: 10 });
    expect(summary.searchable.state).toBe('pending');
    expect(summary.errorCount).toBe(2);
    expect(summary.link).toBeNull();

    const legacy = toJobDetail({
      jobId: 'job-imp-1',
      jobType: 'import',
      status: 'completed',
      initiatedBy: 'u-1',
      targetSnapshotId: 's-1',
      createdAt: '2026-10-04T09:00:00Z',
      finishedAt: '2026-10-04T09:00:09Z',
      committed: {
        chunksTotal: 2,
        chunksCommitted: 1,
        chunksFailed: 1,
        chunksCancelled: 0,
        chunksPending: 0,
      },
      indexed: { indexTasksTotal: 2, indexTasksApplied: 1, state: 'indexing' },
    });
    expect(legacy).toMatchObject({
      type: 'import',
      name: 'Import',
      createdBy: { userId: 'u-1' },
      snapshotId: 's-1',
      completedAt: '2026-10-04T09:00:09Z',
      updatedAt: '2026-10-04T09:00:09Z',
      committed: { done: 2, total: 2 },
      searchable: { done: 1, total: 2, state: 'catchingUp' },
      chunks: { failed: 1, done: 1 },
    });
    expect(toJobEvent({ status: 'running' })).toBeNull();
    expect(toJobEvent({ jobId: 'j', status: 'failed', updatedAt: 'x' })?.status).toBe('failed');
  });
});
