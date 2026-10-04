import { DestroyRef, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ToastService } from '../../ui';
import { toApiError } from '../api/problem-details';
import { SessionService } from '../session/session';
import { WorkspaceContext } from '../workspace/workspace-context';
import {
  type CreatedBy,
  type JobEvent,
  type JobSummary,
  applyEvent,
  canViewAll,
  completionNotice,
  isFinished,
} from './job-model';
import { JobStream, JobsApi } from './jobs-api';

/** Polling interval of the fallback (Q-35: SSE with a polling fallback of at least 5 s). */
export const JOB_POLL_MS = new InjectionToken<number>('JOB_POLL_MS', { factory: () => 5000 });
/** After the browser gives up on the event stream, how long to poll before trying the stream again. */
export const JOB_STREAM_RETRY_MS = new InjectionToken<number>('JOB_STREAM_RETRY_MS', {
  factory: () => 30_000,
});

/** Jobs the tray lists. */
const RECENT = 8;
/** Jobs kept in memory; the oldest settled ones are dropped first. */
const KEEP = 200;

/**
 * `live`: the event stream is connected; `polling`: the fallback is reading `…/jobs?updatedSince=`; `unavailable`:
 * the jobs API refused (not deployed, or no access), so nothing is followed.
 */
export type FeedMode = 'starting' | 'live' | 'polling' | 'unavailable';

/**
 * The job feed of the active workspace, for root-level consumers (the header's job tray). Set and cleared by
 * `JobFeed`, like `ActiveWorkspace`.
 */
@Injectable({ providedIn: 'root' })
export class ActiveJobFeed {
  private readonly _current = signal<JobFeed | null>(null);
  readonly current = this._current.asReadonly();

  enter(feed: JobFeed): void {
    this._current.set(feed);
  }

  leave(feed: JobFeed): void {
    if (this._current() === feed) this._current.set(null);
  }
}

/**
 * Live state of the workspace's jobs (E06-T07, Q-35): the caller's own jobs, or every job with `Job.ViewAll`. Reads
 * the recent jobs once, then follows `GET …/job-events` (server-sent events); while the stream is down it polls
 * `GET …/jobs?updatedSince=`, and after a reconnection it catches up the same way (at most once per polling
 * interval), so a change shows within the stream's latency or one polling interval. When one of these jobs finishes it raises a notification (failures and
 * errors persist until dismissed). Provided by the workspace shell, so a workspace switch ends it.
 */
@Injectable()
export class JobFeed {
  private readonly api = inject(JobsApi);
  private readonly context = inject(WorkspaceContext);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly session = inject(SessionService);
  private readonly pollMs = inject(JOB_POLL_MS);
  private readonly streamRetryMs = inject(JOB_STREAM_RETRY_MS);

  /** Whose jobs are followed and notified. */
  readonly scope: CreatedBy = canViewAll(this.context) ? 'all' : 'me';

  private readonly _jobs = signal<ReadonlyMap<string, JobSummary>>(new Map());
  /** Every job seen, by id, at its latest known state. */
  readonly jobs = this._jobs.asReadonly();
  readonly recent = computed(() =>
    [...this._jobs().values()]
      .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt))
      .slice(0, RECENT),
  );
  readonly running = computed(
    () => [...this._jobs().values()].filter((j) => !isFinished(j.status)).length,
  );
  private readonly _mode = signal<FeedMode>('starting');
  readonly mode = this._mode.asReadonly();

  private stream: JobStream | null = null;
  private streamOpen = false;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private polling = false;
  private lastReadAt = 0;
  private catchUpTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;
  private readonly startedAt = Date.now();
  /**
   * Server time of the newest change seen; polls ask for changes after it (overlap is harmless). Until the first read
   * it trails the browser clock by ten minutes, so a server clock somewhat behind does not hide changes.
   */
  private watermark = new Date(Date.now() - 600_000).toISOString();
  private readonly quiet = new Set<string>();
  private readonly fetching = new Set<string>();

  constructor() {
    const active = inject(ActiveJobFeed);
    active.enter(this);
    inject(DestroyRef).onDestroy(() => {
      this.stop();
      active.leave(this);
    });
    queueMicrotask(() => void this.start());
  }

  get workspaceId(): string {
    return this.context.workspaceId;
  }

  /** A feature that reports this job's outcome itself (e.g. the Mass Edit result) suppresses the notification. */
  reportedElsewhere(jobId: string): void {
    this.quiet.add(jobId);
  }

  /** Reads the changes now, e.g. right after the user retried or cancelled a job. */
  refresh(): Promise<void> {
    return this.poll();
  }

  /** A job read elsewhere (the job page), so the tray shows the same state. */
  observe(job: JobSummary): void {
    this.merge(job, true);
  }

  private async start(): Promise<void> {
    if (this.destroyed) return;
    try {
      this.lastReadAt = Date.now();
      const page = await this.api.list(
        { type: '', status: '', createdBy: this.scope, from: '', to: '' },
        null,
        20,
      );
      if (page.items.length) this.watermark = page.items[0].updatedAt;
      page.items.forEach((job) => this.merge(job, false));
    } catch (e) {
      if (this.refused(e)) return;
      // A passing failure: the stream and the polling fallback keep trying.
    }
    this.connect();
  }

  private connect(): void {
    if (this.destroyed) return;
    this.reconnectTimer = null;
    this.stream = this.api.events({
      open: () => {
        this.streamOpen = true;
        this._mode.set('live');
        this.clearPoll();
        this.catchUp();
      },
      event: (event) => this.onEvent(event),
      error: (fatal) => {
        this.streamOpen = false;
        if (fatal) {
          this.stream?.close();
          this.stream = null;
          this._mode.set('polling');
          this.reconnectTimer = setTimeout(() => this.connect(), this.streamRetryMs);
        }
        this.schedulePoll();
      },
    });
    if (!this.stream) {
      this._mode.set('polling');
      this.schedulePoll();
    }
  }

  /**
   * After a (re)connection, reads what may have been missed while the stream was down, at most once per polling
   * interval: a connection that keeps dropping costs no more requests than polling would.
   */
  private catchUp(): void {
    if (this.catchUpTimer) return;
    const wait = Math.max(0, this.lastReadAt + this.pollMs - Date.now());
    this.catchUpTimer = setTimeout(() => {
      this.catchUpTimer = null;
      void this.poll();
    }, wait);
  }

  private schedulePoll(): void {
    if (this.destroyed || this.pollTimer) return;
    this.pollTimer = setTimeout(async () => {
      this.pollTimer = null;
      if (this.streamOpen || this.destroyed) return;
      this._mode.set('polling');
      await this.poll();
      if (!this.streamOpen) this.schedulePoll();
    }, this.pollMs);
  }

  private clearPoll(): void {
    if (this.pollTimer) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  private async poll(): Promise<void> {
    if (this.polling || this.destroyed || this._mode() === 'unavailable') return;
    this.polling = true;
    this.lastReadAt = Date.now();
    try {
      const page = await this.api.updatedSince(this.watermark, this.scope);
      page.items.forEach((job) => this.merge(job, true));
    } catch (e) {
      this.refused(e);
    } finally {
      this.polling = false;
    }
  }

  private onEvent(event: JobEvent): void {
    this.advance(event.updatedAt);
    const prev = this._jobs().get(event.jobId);
    if (!prev) {
      void this.fetch(event.jobId);
      return;
    }
    const next = applyEvent(prev, event);
    if (!next) return;
    this.store(next);
    this.notify(prev, next);
  }

  /** A job first seen through an event: its name, type and creator come from the job itself. */
  private async fetch(jobId: string): Promise<void> {
    if (this.fetching.has(jobId)) return;
    this.fetching.add(jobId);
    try {
      this.merge(await this.api.get(jobId), true);
    } catch {
      // Not visible to the caller after all (or gone): nothing to show.
    } finally {
      this.fetching.delete(jobId);
    }
  }

  private merge(job: JobSummary, live: boolean): void {
    if (this.destroyed) return;
    this.advance(job.updatedAt);
    const prev = this._jobs().get(job.jobId);
    if (prev && Date.parse(job.updatedAt) < Date.parse(prev.updatedAt)) return;
    this.store(job);
    if (live) this.notify(prev, job);
  }

  private store(job: JobSummary): void {
    this._jobs.update((jobs) => {
      const next = new Map(jobs);
      next.set(job.jobId, job);
      if (next.size > KEEP) {
        const oldest = [...next.values()]
          .filter((j) => isFinished(j.status))
          .sort((a, b) => Date.parse(a.updatedAt) - Date.parse(b.updatedAt))
          .slice(0, next.size - KEEP);
        oldest.forEach((j) => next.delete(j.jobId));
      }
      return next;
    });
  }

  private advance(updatedAt: string): void {
    if (Date.parse(updatedAt) > Date.parse(this.watermark)) this.watermark = updatedAt;
  }

  /** Notifies when a followed job has just finished (not for jobs that were already over when the feed started). */
  private notify(prev: JobSummary | undefined, next: JobSummary): void {
    if (!isFinished(next.status) || this.quiet.has(next.jobId)) return;
    if (prev && isFinished(prev.status)) return;
    if (!prev && Date.parse(next.completedAt ?? next.updatedAt) < this.startedAt) return;
    // Users are notified about their own jobs; Job.ViewAll holders about every job (the stream is scoped the same way).
    if (this.scope === 'me' && next.createdBy.userId !== this.session.principal()?.userId) return;
    const notice = completionNotice(next);
    if (!notice) return;
    const problem = notice.tone === 'error' || notice.tone === 'warning';
    const workspaceId = this.workspaceId;
    this.toasts.show(notice.message, {
      tone: notice.tone,
      action: problem
        ? {
            label: 'View job',
            run: () => void this.router.navigate(['/w', workspaceId, 'jobs', next.jobId]),
          }
        : undefined,
    });
  }

  /** 403/404: the jobs API is not available to this caller, so the feed stops. */
  private refused(e: unknown): boolean {
    const status = toApiError(e).status;
    if (status !== 403 && status !== 404) return false;
    this._mode.set('unavailable');
    this.stop();
    return true;
  }

  private stop(): void {
    this.destroyed = true;
    this.stream?.close();
    this.stream = null;
    this.clearPoll();
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    if (this.catchUpTimer) clearTimeout(this.catchUpTimer);
  }
}
