import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { WorkspaceContext } from '../workspace/workspace-context';
import {
  type CreatedBy,
  type JobDetail,
  type JobEvent,
  type JobFailure,
  type JobFilter,
  type JobSummary,
  type Page,
  filterParams,
  toJobDetail,
  toJobEvent,
  toJobFailure,
  toJobSummary,
} from './job-model';

// The job operations API (wave-9 contract "Job operations", owned by #61) as a port. Written by hand against the
// contract until the routes are in the OpenAPI document; the e2e mock API (e2e/support/mock-jobs.ts) serves the same
// shapes. Provided by the workspace shell, so it is workspace-scoped and replaceable in tests.

/** Callbacks of the live event stream. */
export interface JobStreamHandlers {
  /** The stream is (re)connected; events may have been missed while it was down. */
  open(): void;
  event(event: JobEvent): void;
  /** `fatal`: the browser gave up (e.g. the endpoint answered 404); otherwise it reconnects by itself. */
  error(fatal: boolean): void;
}

export interface JobStream {
  close(): void;
}

@Injectable()
export abstract class JobsApi {
  /** `GET …/jobs?type=&status=&createdBy=&from=&to=&cursor=&limit=`, newest first. */
  abstract list(
    filter: JobFilter,
    cursor?: string | null,
    limit?: number,
  ): Promise<Page<JobSummary>>;
  /** `GET …/jobs?updatedSince=&createdBy=`: the polling fallback of the event stream. */
  abstract updatedSince(since: string, createdBy: CreatedBy): Promise<Page<JobSummary>>;
  /** `GET …/jobs/{jobId}`. */
  abstract get(jobId: string): Promise<JobDetail>;
  /** `GET …/jobs/{jobId}/failures?cursor=`: failed and dead-lettered chunks, tasks and outbox rows. */
  abstract failures(jobId: string, cursor?: string | null): Promise<Page<JobFailure>>;
  /** `POST …/jobs/{jobId}/cancel` (202). */
  abstract cancel(jobId: string): Promise<void>;
  /** `POST …/jobs/{jobId}/retry-failed` (202, idempotent). */
  abstract retryFailed(jobId: string, idempotencyKey: string): Promise<void>;
  /** `GET …/job-events` (text/event-stream); null when the browser cannot stream, so the caller polls. */
  abstract events(handlers: JobStreamHandlers): JobStream | null;
}

const JOBS = 'jobs';

@Injectable()
export class HttpJobsApi extends JobsApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  list(filter: JobFilter, cursor?: string | null, limit = 50): Promise<Page<JobSummary>> {
    const params = { ...filterParams(filter), limit: String(limit), ...(cursor ? { cursor } : {}) };
    return this.page(params);
  }

  updatedSince(since: string, createdBy: CreatedBy): Promise<Page<JobSummary>> {
    return this.page({ updatedSince: since, createdBy, limit: '100' });
  }

  get(jobId: string): Promise<JobDetail> {
    return firstValueFrom(
      this.http.get<unknown>(this.context.apiUrl(JOBS, jobId)).pipe(map(toJobDetail)),
    );
  }

  failures(jobId: string, cursor?: string | null): Promise<Page<JobFailure>> {
    return firstValueFrom(
      this.http
        .get<{ items?: unknown[]; nextCursor?: string | null }>(
          this.context.apiUrl(JOBS, jobId, 'failures'),
          { params: cursor ? { cursor } : {} },
        )
        .pipe(
          map((body) => ({
            items: (body?.items ?? []).map(toJobFailure),
            nextCursor: body?.nextCursor ?? null,
          })),
        ),
    );
  }

  async cancel(jobId: string): Promise<void> {
    await firstValueFrom(this.http.post(this.context.apiUrl(JOBS, jobId, 'cancel'), {}));
  }

  async retryFailed(jobId: string, idempotencyKey: string): Promise<void> {
    await firstValueFrom(
      this.http.post(
        this.context.apiUrl(JOBS, jobId, 'retry-failed'),
        {},
        { headers: { 'Idempotency-Key': idempotencyKey } },
      ),
    );
  }

  events(handlers: JobStreamHandlers): JobStream | null {
    if (typeof EventSource === 'undefined') return null;
    // Same origin, so the session cookie goes with it; the browser reconnects (with Last-Event-ID) by itself.
    const source = new EventSource(this.context.apiUrl('job-events'));
    const onMessage = (e: MessageEvent) => {
      let event: JobEvent | null = null;
      try {
        event = toJobEvent(JSON.parse(String(e.data)));
      } catch {
        return; // Not JSON (a comment or an unknown event shape): ignored, like the tolerant reader elsewhere.
      }
      if (event) handlers.event(event);
    };
    source.addEventListener('open', () => handlers.open());
    source.addEventListener('message', onMessage);
    source.addEventListener('job', onMessage as EventListener);
    source.addEventListener('error', () =>
      handlers.error(source.readyState === EventSource.CLOSED),
    );
    return { close: () => source.close() };
  }

  private page(params: Record<string, string>): Promise<Page<JobSummary>> {
    return firstValueFrom(
      this.http
        .get<{
          items?: unknown[];
          nextCursor?: string | null;
        }>(this.context.apiUrl(JOBS), { params })
        .pipe(
          map((body) => ({
            items: (body?.items ?? []).map(toJobSummary),
            nextCursor: body?.nextCursor ?? null,
          })),
        ),
    );
  }
}
