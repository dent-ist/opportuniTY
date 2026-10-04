import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, describeError, toApiError } from '../../core/api/problem-details';
import { JOB_POLL_MS, JobFeed } from '../../core/jobs/job-feed';
import {
  type JobDetail as Job,
  type JobFailure,
  canCancel,
  canRetry,
  countText,
  etaText,
  failedChunks,
  fraction,
  isFinished,
  isSettled,
  percentText,
  pillStatus,
  searchableText,
  statusLabel,
  typeLabel,
} from '../../core/jobs/job-model';
import { JobsApi } from '../../core/jobs/jobs-api';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  DialogService,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  Progress,
  StatusPill,
  ToastService,
} from '../../ui';
import { LiveNote } from './live-note';

/**
 * One job (E06-T07): status, the two phases Saved (committed in the database) and Searchable (applied to the search
 * index) with count, percent and time left, chunk, attempt and error counts, the failed and dead-lettered chunks with
 * "Retry failed chunks" (`Job.Replay` / `Job.Manage` only), Cancel (the owner or `Job.Cancel` / `Job.Manage`) and the
 * copyable correlation and frozen-set ids support asks for. Follows the job through the workspace's job feed and
 * announces status changes politely.
 */
@Component({
  selector: 'opp-job-detail',
  imports: [
    Badge,
    Button,
    ErrorState,
    Icon,
    IconButton,
    LiveNote,
    LoadingState,
    Progress,
    RouterLink,
    StatusPill,
  ],
  templateUrl: './job-detail.html',
  styleUrl: './jobs.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobDetail {
  /** Route parameter. */
  readonly jobId = input.required<string>();

  private readonly api = inject(JobsApi);
  private readonly feed = inject(JobFeed, { optional: true });
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionService);
  private readonly prefs = inject(UiPreferences);
  private readonly announcer = inject(Announcer);
  private readonly toasts = inject(ToastService);
  private readonly dialogs = inject(DialogService);
  private readonly pollMs = inject(JOB_POLL_MS);

  protected readonly job = signal<Job | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly failures = signal<readonly JobFailure[]>([]);
  protected readonly failuresCursor = signal<string | null>(null);
  protected readonly failuresError = signal<ApiError | null>(null);
  protected readonly retrying = signal(false);
  protected readonly cancelling = signal(false);

  private loading = false;
  private stale = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;
  private retryKey: string | null = null;

  private readonly caller = computed(() => ({
    userId: this.session.principal()?.userId ?? null,
    can: (p: string) => this.context.can(p),
  }));
  protected readonly finished = computed(() => {
    const j = this.job();
    return !!j && isFinished(j.status);
  });
  protected readonly failed = computed(() => {
    const j = this.job();
    return j ? failedChunks(j) : 0;
  });
  protected readonly mayRetry = computed(() => canRetry(this.caller()) && this.failed() > 0);
  protected readonly mayCancel = computed(() => {
    const j = this.job();
    return !!j && canCancel(j, this.caller());
  });
  protected readonly ownPage = computed(() => {
    const link = this.job()?.link;
    return link ? ['/w', this.context.workspaceId, ...link.split('/').filter(Boolean)] : null;
  });

  protected readonly saved = computed(() => {
    const j = this.job();
    if (!j) return null;
    const locale = this.prefs.locale();
    const value = fraction(j.committed, isFinished(j.status));
    const percent = percentText(value, locale);
    const count = countText(j.committed, locale);
    const eta = isFinished(j.status) ? null : etaText(j.etaSeconds);
    return {
      value: value * 100,
      percent,
      count,
      eta,
      spoken: [`${percent} saved`, count, eta].filter(Boolean).join(', '),
    };
  });
  protected readonly searchable = computed(() => {
    const j = this.job();
    if (!j || j.searchable.state === 'notApplicable') return null;
    const locale = this.prefs.locale();
    const value = j.searchable.state === 'current' ? 1 : fraction(j.searchable);
    const percent = percentText(value, locale);
    const count = countText(j.searchable, locale);
    return {
      value: value * 100,
      percent,
      count,
      state: searchableText(j),
      current: j.searchable.state === 'current',
      spoken: `${percent} searchable, ${count}`,
    };
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer) clearTimeout(this.timer);
    });
    // A new job id (the tray links job pages to each other) starts afresh.
    effect(() => {
      this.jobId();
      untracked(() => {
        this.job.set(null);
        this.failures.set([]);
        this.failuresCursor.set(null);
        this.retryKey = null;
        void this.load();
      });
    });
    // Live: every change the feed sees for this job re-reads the job (chunks, attempts, time left).
    effect(() => {
      const seen = this.feed?.jobs().get(this.jobId());
      const shown = untracked(this.job);
      if (seen && shown && Date.parse(seen.updatedAt) > Date.parse(shown.updatedAt)) {
        untracked(() => void this.load());
      }
    });
  }

  protected typeName(job: Job): string {
    return typeLabel(job.type);
  }

  protected pill(job: Job) {
    return pillStatus(job.status);
  }

  protected num(value: number): string {
    return new Intl.NumberFormat(this.prefs.locale()).format(value);
  }

  protected date(value: string | null): string {
    return value
      ? new Intl.DateTimeFormat(this.prefs.locale(), {
          dateStyle: 'medium',
          timeStyle: 'medium',
        }).format(new Date(value))
      : '–';
  }

  protected async load(): Promise<void> {
    if (this.destroyed) return;
    if (this.loading) {
      this.stale = true;
      return;
    }
    this.loading = true;
    const id = this.jobId();
    try {
      const job = await this.api.get(id);
      if (id !== this.jobId()) return;
      const before = this.job();
      this.job.set(job);
      this.error.set(null);
      this.feed?.observe(job);
      if (before) this.announceChange(before, job);
      if (failedChunks(job) > 0 && (!before || failedChunks(before) !== failedChunks(job))) {
        void this.loadFailures(true);
      } else if (failedChunks(job) === 0) {
        this.failures.set([]);
      }
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading = false;
    }
    if (this.stale) {
      this.stale = false;
      void this.load();
      return;
    }
    this.scheduleFallback();
  }

  /** Without a live feed (the jobs list API refused, or none), the page re-reads a running job itself. */
  private scheduleFallback(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const job = this.job();
    const live = this.feed && this.feed.mode() !== 'unavailable';
    if (this.destroyed || live || (job && isSettled(job))) return;
    if (this.error() && !job) return;
    this.timer = setTimeout(() => void this.load(), this.pollMs);
  }

  private announceChange(before: Job, after: Job): void {
    if (before.status !== after.status) {
      this.announcer.announce(`Job status: ${statusLabel(after.status)}`, {
        politeness: 'polite',
      });
    } else if (before.searchable.state !== 'current' && after.searchable.state === 'current') {
      this.announcer.announce('The job’s documents are now searchable', { politeness: 'polite' });
    }
  }

  protected async loadFailures(reset = false): Promise<void> {
    try {
      const page = await this.api.failures(this.jobId(), reset ? null : this.failuresCursor());
      this.failures.update((items) => (reset ? page.items : [...items, ...page.items]));
      this.failuresCursor.set(page.nextCursor);
      this.failuresError.set(null);
    } catch (e) {
      this.failuresError.set(toApiError(e));
    }
  }

  protected async retry(): Promise<void> {
    const job = this.job();
    if (!job || this.retrying()) return;
    this.retrying.set(true);
    // One key per retry intent: repeating the request after a network failure never queues the chunks twice.
    this.retryKey ??= crypto.randomUUID();
    try {
      await this.api.retryFailed(job.jobId, this.retryKey);
      this.retryKey = null;
      const n = this.failed();
      this.toasts.show(
        `Retrying ${this.num(n)} failed chunk${n === 1 ? '' : 's'} of “${job.name}”.`,
        { tone: 'info' },
      );
      await this.afterAction();
    } catch (e) {
      this.actionFailed('Retry failed chunks', e);
    } finally {
      this.retrying.set(false);
    }
  }

  protected async cancel(): Promise<void> {
    const job = this.job();
    if (!job || this.cancelling()) return;
    const confirmed = await this.dialogs.confirm({
      title: 'Cancel this job?',
      message:
        `“${job.name}” stops after the chunks in progress. What is already saved stays saved; ` +
        'the remaining chunks are not processed.',
      confirmLabel: 'Cancel job',
      cancelLabel: 'Keep running',
      tone: 'danger',
    });
    if (!confirmed) return;
    this.cancelling.set(true);
    try {
      await this.api.cancel(job.jobId);
      this.toasts.show(`Cancelling “${job.name}”.`, { tone: 'info' });
      await this.afterAction();
    } catch (e) {
      this.actionFailed('Cancel', e);
    } finally {
      this.cancelling.set(false);
    }
  }

  private async afterAction(): Promise<void> {
    await this.load();
    void this.feed?.refresh();
  }

  private actionFailed(action: string, e: unknown): void {
    const { title, detail } = describeError(toApiError(e));
    this.toasts.show(`${action} did not work. ${title}. ${detail}`, { tone: 'error' });
  }

  protected async copy(label: string, value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      this.toasts.show(`${label} copied.`, { tone: 'success', durationMs: 3000 });
    } catch {
      this.toasts.show(`${label} could not be copied. Select it and copy it instead.`, {
        tone: 'warning',
      });
    }
  }
}
