import {
  DestroyRef,
  Injectable,
  InjectionToken,
  Signal,
  WritableSignal,
  inject,
  signal,
} from '@angular/core';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { JobFeed } from '../../../core/jobs/job-feed';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { ToastService } from '../../../ui';
import { BulkCodingApi, BulkJob, isFinished } from './bulk-coding-api';

/** How often running jobs and materializing frozen sets are polled. */
export const MASS_EDIT_POLL_MS = new InjectionToken<number>('MASS_EDIT_POLL_MS', {
  factory: () => 1000,
});

/** One submitted Mass Edit job, followed until it is saved and searchable. */
export interface TrackedJob {
  readonly jobId: string;
  readonly documentCount: number;
  readonly job: Signal<BulkJob | null>;
  readonly error: Signal<ApiError | null>;
  /** While a dialog shows the job, it reports the outcome; otherwise a toast with the Mass Edit counts does. */
  attached: boolean;
}

interface Entry extends TrackedJob {
  readonly job: WritableSignal<BulkJob | null>;
  readonly error: WritableSignal<ApiError | null>;
  done: boolean;
}

/**
 * Follows submitted Mass Edit jobs through `GET …/jobs/{id}` (two phases: Saved, then Searchable). Lives with the
 * Documents page, so a job keeps being followed after its dialog closes; on completion an unattached job is
 * reported in a toast with Updated / Skipped / Failed.
 */
@Injectable()
export class MassEditJobs {
  private readonly api = inject(BulkCodingApi);
  private readonly toasts = inject(ToastService);
  private readonly prefs = inject(UiPreferences);
  private readonly pollMs = inject(MASS_EDIT_POLL_MS);
  /** The job tray would notify too; Mass Edit reports its own outcome (Updated / Skipped / Failed). */
  private readonly feed = inject(JobFeed, { optional: true });
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private destroyed = false;
  private readonly _tracked = signal<readonly TrackedJob[]>([]);
  /** Jobs submitted from this page, oldest first (the Documents banner shows those not yet searchable). */
  readonly tracked = this._tracked.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.timers.forEach(clearTimeout);
    });
  }

  track(jobId: string, documentCount: number): TrackedJob {
    const entry: Entry = {
      jobId,
      documentCount,
      job: signal(null),
      error: signal(null),
      attached: true,
      done: false,
    };
    this.feed?.reportedElsewhere(jobId);
    this._tracked.update((jobs) => [...jobs, entry]);
    void this.poll(entry);
    return entry;
  }

  private async poll(entry: Entry): Promise<void> {
    if (this.destroyed) return;
    let failures = 0;
    try {
      const job = await this.api.job(entry.jobId);
      entry.job.set(job);
      entry.error.set(null);
      const finished = isFinished(job);
      if (finished && !entry.done) {
        entry.done = true;
        if (!entry.attached) this.report(job);
      }
      // Keep following the Searchable phase after the job saved everything.
      if (finished && (job.searchable || job.status === 'failed' || job.status === 'cancelled'))
        return;
    } catch (e) {
      const error = toApiError(e);
      entry.error.set(error);
      if (error.status === 404 || error.status === 403) return;
      failures++;
    }
    if (this.destroyed) return;
    const timer = setTimeout(
      () => {
        this.timers.delete(timer);
        void this.poll(entry);
      },
      this.pollMs * (failures ? 3 : 1),
    );
    this.timers.add(timer);
  }

  private report(job: BulkJob): void {
    const tone = job.status === 'completed' && job.failed === 0 ? 'success' : 'warning';
    this.toasts.show(`Mass Edit finished. ${outcomeText(job, this.prefs.locale())}`, { tone });
  }
}

/** "Updated 58,201 · Skipped 129 · Failed 0" (+ unchanged and no-longer-permitted when there are any). */
export function outcomeText(job: BulkJob, locale: string): string {
  const n = new Intl.NumberFormat(locale);
  const parts = [
    `Updated ${n.format(job.applied)}`,
    `Skipped ${n.format(job.skipped)}`,
    `Failed ${n.format(job.failed)}`,
  ];
  if (job.unchanged) parts.push(`Already set ${n.format(job.unchanged)}`);
  if (job.excluded) parts.push(`No longer permitted ${n.format(job.excluded)}`);
  return parts.join(' · ');
}
