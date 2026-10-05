import { Injectable, computed, inject, signal } from '@angular/core';
import { JobFeed } from '../../../core/jobs/job-feed';
import {
  type JobSummary,
  fraction,
  isFinished,
  percentText,
  typeLabel,
} from '../../../core/jobs/job-model';
import { SessionService } from '../../../core/session/session';
import { type BulkJob, isFinished as bulkFinished } from '../mass-edit/bulk-coding-api';
import { MassEditJobs } from '../mass-edit/mass-edit-jobs';

/**
 * A job the reviewer started that is saved (committed) but not yet searchable: the Documents banner
 * "Mass Edit saved · Search index updating… (63%)" (E16-T07), until the index catches up with it.
 */
export interface PendingJob {
  readonly jobId: string;
  /** "Mass Edit", "Import “VOL001.dat 2026-10-01”". */
  readonly title: string;
  /** Searchable progress 0–1, or null when the job does not say. */
  readonly progress: number | null;
  /** The job's last committed generation (admin detail only, Q-10). */
  readonly jobGeneration: string | null;
}

/** Job types whose changes show in the document list: bulk coding, imports (overlays are imports). */
const LIST_CHANGING = new Set(['bulkCoding', 'import']);

export function jobTitle(type: string, name: string): string {
  return type === 'bulkCoding' || !name ? typeLabel(type) : `${typeLabel(type)} “${name}”`;
}

/** "Mass Edit saved · Search index updating… (63%)". */
export function bannerText(job: PendingJob, locale: string): string {
  const progress = job.progress === null ? '' : ` (${percentText(job.progress, locale)})`;
  return `${job.title} saved · Search index updating…${progress}`;
}

/** Whether a job from the feed is the reviewer's, saved and still catching up; null when it is searchable or not. */
export function fromSummary(job: JobSummary, userId: string | null): PendingJob | null {
  if (!LIST_CHANGING.has(job.type) || !userId || job.createdBy.userId !== userId) return null;
  if (!isFinished(job.status) || job.status === 'failed') return null;
  if (job.status === 'cancelled' && job.committed.done === 0) return null;
  if (job.searchable.state === 'current' || job.searchable.state === 'notApplicable') return null;
  return {
    jobId: job.jobId,
    title: jobTitle(job.type, job.name),
    progress: job.searchable.total ? fraction(job.searchable) : null,
    jobGeneration: job.searchable.jobGeneration ?? null,
  };
}

/** The same for a Mass Edit job followed from this page. */
export function fromBulkJob(job: BulkJob): PendingJob | null {
  if (!bulkFinished(job) || job.status === 'failed' || job.searchable) return null;
  if (job.status === 'cancelled' && job.applied === 0) return null;
  return {
    jobId: job.jobId,
    title: jobTitle('bulkCoding', ''),
    progress: job.indexTasksTotal
      ? Math.min(1, Math.max(0, job.indexTasksApplied / job.indexTasksTotal))
      : null,
    jobGeneration: job.jobGeneration,
  };
}

/**
 * The reviewer's jobs that are saved but not yet searchable: from the workspace job feed (imports, overlays and Mass
 * Edits started anywhere) and from the Mass Edit jobs followed by this page. A job leaves the list as soon as any
 * source reports it searchable. Provided by the Documents page.
 */
@Injectable()
export class PendingSearchJobs {
  private readonly feed = inject(JobFeed, { optional: true });
  private readonly massEdit = inject(MassEditJobs, { optional: true });
  private readonly session = inject(SessionService);
  private readonly dismissed = signal<ReadonlySet<string>>(new Set());

  readonly jobs = computed<readonly PendingJob[]>(() => {
    const userId = this.session.principal()?.userId ?? null;
    const pending = new Map<string, PendingJob>();
    const caughtUp = new Set<string>();
    for (const tracked of this.massEdit?.tracked() ?? []) {
      const job = tracked.job();
      if (!job) continue;
      const entry = fromBulkJob(job);
      if (entry) pending.set(entry.jobId, entry);
      else if (bulkFinished(job)) caughtUp.add(job.jobId);
    }
    for (const job of this.feed?.jobs().values() ?? []) {
      const entry = fromSummary(job, userId);
      // The feed has the job's name and generation; it wins over the page's own copy unless that one caught up.
      if (entry) pending.set(entry.jobId, entry);
      else if (isFinished(job.status)) caughtUp.add(job.jobId);
    }
    const dismissed = this.dismissed();
    return [...pending.values()].filter((j) => !caughtUp.has(j.jobId) && !dismissed.has(j.jobId));
  });

  wasDismissed(jobId: string): boolean {
    return this.dismissed().has(jobId);
  }

  /** Hides a job's banner for this visit (it stays on the Jobs page and in the tray). */
  dismiss(jobId: string): void {
    this.dismissed.update((ids) => new Set(ids).add(jobId));
  }
}
