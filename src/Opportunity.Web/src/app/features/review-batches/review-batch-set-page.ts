import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import type { ReviewBatchResource, ReviewBatchSetResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { Badge, Button, ErrorState, Icon, LoadingState } from '../../ui';
import { BatchActions } from './batch-actions';
import { HttpReviewBatchApi, ReviewBatchApi } from './review-batch-api';
import { BATCH_STATUS, assigneeText, orderText } from './review-batch-model';

/**
 * One Batch Set (#178): its settings and every batch with status (Pending · In progress · Completed), reviewer and the
 * documents you may see. Reviewers check out the next pending batch (or a chosen one) and check theirs in; managers
 * (ReviewBatch.Manage) assign, reassign and release batches. Every change is audited by the API.
 */
@Component({
  selector: 'opp-review-batch-set-page',
  imports: [Badge, Button, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './review-batch-set-page.html',
  styleUrls: ['../exports/delivery.scss', './review-batches.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [{ provide: ReviewBatchApi, useClass: HttpReviewBatchApi }, BatchActions],
})
export class ReviewBatchSetPage {
  private readonly api = inject(ReviewBatchApi);
  private readonly actions = inject(BatchActions);
  private readonly prefs = inject(UiPreferences);
  private readonly context = inject(WorkspaceContext);
  private readonly principal = inject(SessionService).principal;
  private readonly batchSetId = inject(ActivatedRoute).snapshot.paramMap.get('batchSetId') ?? '';

  protected readonly canCode = this.context.can(PERMISSIONS.codingWrite);
  protected readonly canManage = this.context.can(PERMISSIONS.reviewBatchManage);
  protected readonly status = BATCH_STATUS;
  protected readonly myId = computed(() => this.principal()?.userId ?? null);

  protected readonly set = signal<ReviewBatchSetResource | null>(null);
  protected readonly batches = signal<readonly ReviewBatchResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.error.set(null);
    try {
      const [set, page] = await Promise.all([
        this.api.set(this.batchSetId),
        this.api.batches({ batchSetId: this.batchSetId }),
      ]);
      this.set.set(set);
      this.batches.set(page.items);
      this.nextCursor.set(page.nextCursor);
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
      const page = await this.api.batches({ batchSetId: this.batchSetId }, this.nextCursor());
      this.batches.update((batches) => [...batches, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected isMine(batch: ReviewBatchResource): boolean {
    return batch.status === 'checkedOut' && batch.assignee?.userId === this.myId();
  }

  protected order(set: ReviewBatchSetResource): string {
    return orderText(set.sort ?? 'controlNumber', !!set.sortDescending);
  }

  protected assignee(batch: ReviewBatchResource): string {
    return assigneeText(batch, this.myId());
  }

  protected review(batch: ReviewBatchResource): void {
    void this.actions.review(batch.batchId);
  }

  protected async checkOutNext(): Promise<void> {
    await this.act('next', async () => {
      if (!(await this.actions.checkOutNext(this.batchSetId))) await this.load();
    });
  }

  protected async checkOut(batch: ReviewBatchResource): Promise<void> {
    await this.act(batch.batchId, async () => {
      const taken = await this.actions.checkOut(batch);
      if (taken) await this.actions.review(taken.batchId);
      else await this.load();
    });
  }

  protected checkIn(batch: ReviewBatchResource): Promise<void> {
    return this.change(batch, () => this.actions.checkIn(batch));
  }

  protected assign(batch: ReviewBatchResource): Promise<void> {
    return this.change(batch, () => this.actions.assign(batch));
  }

  protected release(batch: ReviewBatchResource): Promise<void> {
    return this.change(batch, () => this.actions.release(batch));
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined
      ? '—'
      : Number(value).toLocaleString(this.prefs.locale());
  }

  protected date(value: unknown): string {
    return value ? this.dates().format(new Date(String(value))) : '';
  }

  /** Runs a batch action, then shows every batch and the counts as they are now. */
  private change(
    batch: ReviewBatchResource,
    action: () => Promise<ReviewBatchResource | null>,
  ): Promise<void> {
    return this.act(batch.batchId, async () => {
      await action();
      await this.load();
    });
  }

  private async act(key: string, action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(key);
    try {
      await action();
    } finally {
      this.busy.set(null);
    }
  }
}
