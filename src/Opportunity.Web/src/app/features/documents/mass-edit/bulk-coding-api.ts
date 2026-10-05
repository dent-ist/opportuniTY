import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { getJob } from '../../../core/api/generated/fn/jobs/get-job';
import { createSnapshot } from '../../../core/api/generated/fn/snapshots/create-snapshot';
import { getSnapshot } from '../../../core/api/generated/fn/snapshots/get-snapshot';
import type {
  CreateSnapshotRequest,
  JobDetail,
  JobResource,
  JobSearchableProgress,
  JobResourceStatus,
  SnapshotResource,
  SnapshotResourceStatus,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import type { SelectionTarget } from '../grid/selection';

// Mass Edit's backends as a port (E16-T06), so the dialog is built while the bulk coding job (E10-T04, #92) lands:
// frozen sets (`POST/GET …/snapshots`, #90), the bulk coding job (`POST …/bulk-coding`, assumed shape below) and
// job progress (`GET …/jobs/{id}`). Provided by the Documents page; the e2e mock API serves the same shapes.

/** One change to one field across the frozen set. Choice values are choice ids (ADR-003: ids survive renames). */
export type BulkOperation = 'set' | 'addChoices' | 'removeChoices' | 'clear';

export interface BulkFieldChange {
  readonly fieldId: string;
  readonly operation: BulkOperation;
  /** `set`: the value (choice id, choice ids for Multiple Choice); `addChoices`/`removeChoices`: choice ids. */
  readonly value?: unknown;
}

/** A frozen set (snapshot) as the confirmation shows it. */
export interface FrozenSet {
  readonly snapshotId: string;
  readonly status: SnapshotResourceStatus;
  /** Documents frozen; null while a large set is still materializing. */
  readonly documentCount: number | null;
  /** Search generation the membership was read at (shown to admin and support roles only, Q-10). */
  readonly searchGeneration: string | null;
  /** When the membership was read (ISO-8601). */
  readonly frozenAt: string | null;
  /** Some selected documents had changes still being indexed (ADR-002 §4). */
  readonly selectedWhileIndexing: boolean;
  readonly statusReason: string | null;
  /** Documents added by "Include: Family / Duplicates / Email thread" before freezing (E09-T03). */
  readonly related: number;
}

/** Progress and outcome of a bulk coding job, in both phases ("Saved", then "Searchable"). */
export interface BulkJob {
  readonly jobId: string;
  readonly status: JobResourceStatus;
  readonly statusReason: string | null;
  readonly applied: number;
  readonly unchanged: number;
  /** Q-07: changed by someone else after the job started, left as they were. */
  readonly skipped: number;
  readonly failed: number;
  /** No longer codable by the submitter (access changed after freezing). */
  readonly excluded: number;
  readonly indexTasksApplied: number;
  readonly indexTasksTotal: number;
  readonly searchable: boolean;
  /** The job's last committed generation (wave-10 `searchable.jobGeneration`; shown to admins only, Q-10). */
  readonly jobGeneration: string | null;
}

export interface BulkCodingSubmission {
  readonly snapshotId: string;
  readonly changes: readonly BulkFieldChange[];
}

@Injectable()
export abstract class BulkCodingApi {
  /** Freezes the selection for bulk coding; the result may still be `materializing` (202). */
  abstract freeze(target: SelectionTarget, idempotencyKey: string): Promise<FrozenSet>;
  abstract frozenSet(snapshotId: string): Promise<FrozenSet>;
  /** Starts the job; answers as soon as it is queued (202). */
  abstract submit(submission: BulkCodingSubmission, idempotencyKey: string): Promise<string>;
  abstract job(jobId: string): Promise<BulkJob>;
}

/** HTTP adapter: the generated client for snapshots and jobs; the bulk coding route by hand until #92's contract. */
@Injectable()
export class HttpBulkCodingApi extends BulkCodingApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  freeze(target: SelectionTarget, idempotencyKey: string): Promise<FrozenSet> {
    // "All results" goes as the query, never as ids (E16-T06); checked rows as ids (at most MAX_SELECTED_IDS).
    const body: CreateSnapshotRequest =
      target.kind === 'all'
        ? {
            purpose: 'bulkCoding',
            query: target.query,
            ...(target.expand ? { expand: target.expand } : {}),
          }
        : { purpose: 'bulkCoding', documentIds: [...target.documentIds] };
    return firstValueFrom(
      createSnapshot(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        'Idempotency-Key': idempotencyKey,
        body,
      }).pipe(map((r) => toFrozenSet(r.body))),
    );
  }

  frozenSet(snapshotId: string): Promise<FrozenSet> {
    return firstValueFrom(
      getSnapshot(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        snapshotId,
      }).pipe(map((r) => toFrozenSet(r.body))),
    );
  }

  submit(submission: BulkCodingSubmission, idempotencyKey: string): Promise<string> {
    return firstValueFrom(
      this.http
        .post<Pick<JobResource, 'jobId'>>(this.context.apiUrl('bulk-coding'), submission, {
          headers: { 'Idempotency-Key': idempotencyKey },
        })
        .pipe(map((job) => job.jobId)),
    );
  }

  job(jobId: string): Promise<BulkJob> {
    return firstValueFrom(
      getJob(this.http, this.rootUrl, { workspaceId: this.context.workspaceId, jobId }).pipe(
        map((r) => toBulkJob(r.body)),
      ),
    );
  }
}

export function toFrozenSet(s: SnapshotResource): FrozenSet {
  return {
    snapshotId: s.snapshotId,
    status: s.status,
    documentCount: s.documentCount === null ? null : Number(s.documentCount),
    searchGeneration: s.searchGeneration === null ? null : String(s.searchGeneration),
    frozenAt: (s.selectedAt ?? s.materializedAt ?? s.createdAt ?? null) as string | null,
    selectedWhileIndexing: s.selectedWhileIndexing === true,
    statusReason: s.statusReason,
    related: ['Family', 'Duplicate', 'Thread'].reduce(
      (sum, reason) => sum + Number(s.inclusionCounts?.[reason] ?? 0),
      0,
    ),
  };
}

export function toBulkJob(j: JobResource): BulkJob {
  const c = j.committed;
  // The job operations shape (#61) carries `searchable`, which turns current only once the refresh-aware watermark
  // reached the job's generation (#70); the M1 `indexed` block is the fallback.
  const searchable = (j as Partial<JobDetail>).searchable as
    (JobSearchableProgress & { jobGeneration?: unknown }) | undefined;
  return {
    jobId: j.jobId,
    status: j.status,
    statusReason: j.statusReason,
    applied: Number(c.itemsApplied),
    unchanged: Number(c.itemsUnchanged),
    skipped: Number(c.itemsSkippedConcurrentEdit),
    failed: Number(c.itemsFailed),
    excluded: Number(c.itemsExcludedNoAccess),
    indexTasksApplied: Number(searchable?.done ?? j.indexed.indexTasksApplied),
    indexTasksTotal: Number(searchable?.total ?? j.indexed.indexTasksTotal),
    searchable: searchable?.state
      ? searchable.state === 'current' || searchable.state === 'notApplicable'
      : j.indexed.state === 'current',
    jobGeneration:
      searchable?.jobGeneration === null || searchable?.jobGeneration === undefined
        ? null
        : String(searchable.jobGeneration),
  };
}

const FINISHED: readonly JobResourceStatus[] = [
  'completed',
  'completedWithErrors',
  'failed',
  'cancelled',
];

/** The "Saved" phase is over: every document was applied, left unchanged, skipped or failed. */
export function isFinished(job: BulkJob): boolean {
  return FINISHED.includes(job.status);
}

/** Documents the job has dealt with so far. */
export function processed(job: BulkJob): number {
  return job.applied + job.unchanged + job.skipped + job.failed + job.excluded;
}
