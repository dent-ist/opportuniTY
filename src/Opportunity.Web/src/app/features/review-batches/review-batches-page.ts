import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ReviewBatchResource, ReviewBatchSetResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { Badge, Button, EmptyState, ErrorState, Icon, LoadingState } from '../../ui';
import { BatchActions } from './batch-actions';
import { HttpReviewBatchApi, ReviewBatchApi } from './review-batch-api';
import { BATCH_STATUS } from './review-batch-model';
import { DisplayFormat } from '../../core/i18n';

/**
 * Review Batches (#178, Q-53, familiarity guide §2.3/§5.3): the reviewer's batches in progress (Review, Check in) and
 * the Batch Sets with their batches by status (Pending · In progress · Completed). "Check out next batch" takes the
 * next pending batch of a set and opens it in Review mode. Managers (ReviewBatch.Manage) create Batch Sets from a
 * saved search; a set's page lists its batches for assignment and release.
 */
@Component({
  selector: 'opp-review-batches-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './review-batches-page.html',
  styleUrls: ['../exports/delivery.scss', './review-batches.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [{ provide: ReviewBatchApi, useClass: HttpReviewBatchApi }, BatchActions],
})
export class ReviewBatchesPage {
  private readonly api = inject(ReviewBatchApi);
  private readonly actions = inject(BatchActions);
  private readonly fmt = inject(DisplayFormat);
  private readonly context = inject(WorkspaceContext);

  protected readonly canCode = this.context.can(PERMISSIONS.codingWrite);
  protected readonly canManage = this.context.can(PERMISSIONS.reviewBatchManage);
  protected readonly status = BATCH_STATUS;

  protected readonly mine = signal<readonly ReviewBatchResource[]>([]);
  protected readonly sets = signal<readonly ReviewBatchSetResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly busySet = signal<string | null>(null);
  protected readonly error = signal<ApiError | null>(null);

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [mine, sets] = await Promise.all([
        this.api.batches({ assigneeId: 'me', status: 'checkedOut' }),
        this.api.sets(),
      ]);
      this.mine.set(mine.items);
      this.sets.set(sets.items);
      this.nextCursor.set(sets.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    if (this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.sets(this.nextCursor());
      this.sets.update((sets) => [...sets, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected async checkOutNext(set: ReviewBatchSetResource): Promise<void> {
    if (this.busySet()) return;
    this.busySet.set(set.batchSetId);
    try {
      if (!(await this.actions.checkOutNext(set.batchSetId))) await this.load();
    } finally {
      this.busySet.set(null);
    }
  }

  protected review(batch: ReviewBatchResource): void {
    void this.actions.review(batch.batchId);
  }

  protected async checkIn(batch: ReviewBatchResource): Promise<void> {
    await this.actions.checkIn(batch);
    await this.load();
  }

  protected hasPending(set: ReviewBatchSetResource): boolean {
    return Number(set.statusCounts.available) > 0;
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined ? '—' : this.fmt.number(Number(value));
  }

  protected date(value: unknown): string {
    return value ? this.fmt.date(String(value)) : '';
  }
}
