import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  CreateReviewBatchSetRequest,
  CursorPageOfReviewBatchResource,
  CursorPageOfReviewBatchSetResource,
  ReviewBatchAssigneeCandidateList,
  ReviewBatchResource,
  ReviewBatchSetResource,
  ReviewBatchStatusResource,
  ReviewBatchUserResource,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

// Review Batches (#178, Q-53, familiarity guide §5.3) over the review-batch API (#93): Batch Sets cut from a frozen set,
// "check out next batch", check in, and a manager's release and reassignment. Provided by the pages that use it, so it
// is workspace-scoped and replaceable in tests.

export interface BatchFilter {
  readonly batchSetId?: string;
  readonly status?: ReviewBatchStatusResource;
  /** A user ID, or `me`. */
  readonly assigneeId?: string;
}

export interface Page<T> {
  readonly items: readonly T[];
  readonly nextCursor: string | null;
}

@Injectable()
export abstract class ReviewBatchApi {
  abstract sets(cursor?: string | null): Promise<Page<ReviewBatchSetResource>>;
  abstract set(batchSetId: string): Promise<ReviewBatchSetResource>;
  abstract createSet(
    request: CreateReviewBatchSetRequest,
    idempotencyKey: string,
  ): Promise<ReviewBatchSetResource>;
  abstract batches(filter: BatchFilter, cursor?: string | null): Promise<Page<ReviewBatchResource>>;
  abstract batch(batchId: string): Promise<ReviewBatchResource>;
  /** The next available batch of the set, checked out to the caller (or the one of the set they already hold). */
  abstract checkOutNext(batchSetId: string): Promise<ReviewBatchResource>;
  abstract checkOut(batch: ReviewBatchResource): Promise<ReviewBatchResource>;
  abstract checkIn(batch: ReviewBatchResource, completed: boolean): Promise<ReviewBatchResource>;
  /** A reviewer to check the batch out to, or null to make it available again (release). */
  abstract assign(
    batch: ReviewBatchResource,
    assigneeId: string | null,
  ): Promise<ReviewBatchResource>;
  abstract candidates(batchSetId: string): Promise<readonly ReviewBatchUserResource[]>;
}

@Injectable()
export class HttpReviewBatchApi extends ReviewBatchApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async sets(cursor: string | null = null): Promise<Page<ReviewBatchSetResource>> {
    const page = await firstValueFrom(
      this.http.get<CursorPageOfReviewBatchSetResource>(this.context.apiUrl('review-batch-sets'), {
        params: { limit: 100, ...(cursor ? { cursor } : {}) },
      }),
    );
    return { items: page.items, nextCursor: page.nextCursor ?? null };
  }

  set(batchSetId: string): Promise<ReviewBatchSetResource> {
    return firstValueFrom(
      this.http.get<ReviewBatchSetResource>(this.context.apiUrl('review-batch-sets', batchSetId)),
    );
  }

  createSet(
    request: CreateReviewBatchSetRequest,
    idempotencyKey: string,
  ): Promise<ReviewBatchSetResource> {
    return firstValueFrom(
      this.http.post<ReviewBatchSetResource>(this.context.apiUrl('review-batch-sets'), request, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  async batches(
    filter: BatchFilter,
    cursor: string | null = null,
  ): Promise<Page<ReviewBatchResource>> {
    const params: Record<string, string | number> = { limit: 200 };
    if (filter.batchSetId) params['batchSetId'] = filter.batchSetId;
    if (filter.status) params['status'] = filter.status;
    if (filter.assigneeId) params['assigneeId'] = filter.assigneeId;
    if (cursor) params['cursor'] = cursor;
    const page = await firstValueFrom(
      this.http.get<CursorPageOfReviewBatchResource>(this.context.apiUrl('review-batches'), {
        params,
      }),
    );
    return { items: page.items, nextCursor: page.nextCursor ?? null };
  }

  batch(batchId: string): Promise<ReviewBatchResource> {
    return firstValueFrom(
      this.http.get<ReviewBatchResource>(this.context.apiUrl('review-batches', batchId)),
    );
  }

  checkOutNext(batchSetId: string): Promise<ReviewBatchResource> {
    return firstValueFrom(
      this.http.post<ReviewBatchResource>(
        this.context.apiUrl('review-batch-sets', batchSetId, 'check-out-next'),
        null,
      ),
    );
  }

  checkOut(batch: ReviewBatchResource): Promise<ReviewBatchResource> {
    return firstValueFrom(
      this.http.post<ReviewBatchResource>(
        this.context.apiUrl('review-batches', batch.batchId, 'check-out'),
        null,
        { headers: ifMatch(batch) },
      ),
    );
  }

  checkIn(batch: ReviewBatchResource, completed: boolean): Promise<ReviewBatchResource> {
    return firstValueFrom(
      this.http.post<ReviewBatchResource>(
        this.context.apiUrl('review-batches', batch.batchId, 'check-in'),
        { completed },
        { headers: ifMatch(batch) },
      ),
    );
  }

  assign(batch: ReviewBatchResource, assigneeId: string | null): Promise<ReviewBatchResource> {
    return firstValueFrom(
      this.http.put<ReviewBatchResource>(
        this.context.apiUrl('review-batches', batch.batchId, 'assignment'),
        { assigneeId },
        { headers: ifMatch(batch) },
      ),
    );
  }

  async candidates(batchSetId: string): Promise<readonly ReviewBatchUserResource[]> {
    const list = await firstValueFrom(
      this.http.get<ReviewBatchAssigneeCandidateList>(
        this.context.apiUrl('review-batch-sets', batchSetId, 'assignee-candidates'),
        { params: { limit: 500 } },
      ),
    );
    return list.items;
  }
}

/** Status changes are conditional on the batch as it was read (its ETag is its version). */
function ifMatch(batch: ReviewBatchResource): Record<string, string> {
  return { 'If-Match': `"${batch.version}"` };
}
