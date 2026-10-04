import { _IdGenerator } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { Icon, JobStatus, StatusPill } from '../../../ui';
import { BulkJob, isFinished, processed } from './bulk-coding-api';
import { TrackedJob } from './mass-edit-jobs';

const STATUS: Record<BulkJob['status'], JobStatus> = {
  created: 'queued',
  preparing: 'queued',
  running: 'running',
  paused: 'running',
  cancelling: 'running',
  cancelled: 'cancelled',
  completed: 'succeeded',
  completedWithErrors: 'partial',
  failed: 'failed',
};

/**
 * Progress of a submitted Mass Edit in two phases (familiarity guide §3.6, E16-T07 labels): Saved n of N, then
 * Searchable; when the job finishes, the outcome Updated / Skipped (Q-07) / Failed.
 */
@Component({
  selector: 'opp-mass-edit-progress',
  imports: [Icon, StatusPill],
  templateUrl: './mass-edit-progress.html',
  styleUrl: './mass-edit-progress.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MassEditProgress {
  readonly tracked = input.required<TrackedJob>();
  /** Documents in the frozen set. */
  readonly count = input.required<number>();

  private readonly prefs = inject(UiPreferences);
  protected readonly ids = inject(_IdGenerator).getId('opp-mass-edit-progress-');
  protected readonly n = computed(() => new Intl.NumberFormat(this.prefs.locale()));

  protected readonly job = computed(() => this.tracked().job());
  protected readonly jobError = computed(() => this.tracked().error());
  protected readonly jobStatus = computed<JobStatus>(() => {
    const job = this.job();
    return job ? STATUS[job.status] : 'queued';
  });
  protected readonly finished = computed(() => {
    const job = this.job();
    return !!job && isFinished(job);
  });
  protected readonly saved = computed(() => {
    const job = this.job();
    return job ? Math.min(processed(job), this.count()) : 0;
  });
  protected readonly indexed = computed(() => {
    const job = this.job();
    const max = Math.max(1, job?.indexTasksTotal ?? 0);
    return { max, value: job?.searchable ? max : Math.min(max, job?.indexTasksApplied ?? 0) };
  });
}
