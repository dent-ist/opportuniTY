import { Injectable, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import type { ReviewBatchResource } from '../../core/api/generated/models';
import { toApiError } from '../../core/api/problem-details';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { DialogService, ToastService } from '../../ui';
import { AssignBatchDialog, AssignDialogData, CheckInChoice, CheckInDialog } from './batch-dialogs';
import { ReviewBatchApi } from './review-batch-api';
import { BATCH_PARAM, batchErrorText } from './review-batch-model';

/**
 * The batch actions shared by the Review Batches pages and Review mode (#178): open a batch in Review mode, check out
 * (the next one), check in, assign and release. Each returns the batch as it is now, or null when nothing changed
 * (cancelled, or refused: the reason is shown as a toast). Provided next to {@link ReviewBatchApi}.
 */
@Injectable()
export class BatchActions {
  private readonly api = inject(ReviewBatchApi);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;

  /** Documents, bound to the batch: its list, opened in Review mode on its first document. */
  review(batchId: string): Promise<boolean> {
    return this.router.navigate(['/w', this.workspaceId, 'documents'], {
      queryParams: { [BATCH_PARAM]: batchId },
    });
  }

  /** "Check out next batch": the set's next available batch (or the one already held), opened in Review mode. */
  async checkOutNext(batchSetId: string): Promise<ReviewBatchResource | null> {
    try {
      const batch = await this.api.checkOutNext(batchSetId);
      this.toasts.show(`Batch ${batch.name} is checked out to you.`, { tone: 'success' });
      await this.review(batch.batchId);
      return batch;
    } catch (e) {
      const error = toApiError(e);
      this.toasts.show(
        error.status === 409
          ? 'No batch of this Batch Set is pending. Every batch is in progress or completed.'
          : (batchErrorText(error) ?? 'The batch could not be checked out. Try again.'),
        { tone: 'error' },
      );
      return null;
    }
  }

  async checkOut(batch: ReviewBatchResource): Promise<ReviewBatchResource | null> {
    return this.run(() => this.api.checkOut(batch), `Batch ${batch.name} is checked out to you.`);
  }

  /** Asks completed or returned, then checks the batch in. */
  async checkIn(batch: ReviewBatchResource): Promise<ReviewBatchResource | null> {
    const ref = this.dialogs.open<CheckInChoice>(CheckInDialog, {
      data: { batchName: batch.name },
      autoFocus: '[data-autofocus]',
    });
    const choice = await firstValueFrom(ref.closed);
    if (!choice) return null;
    const completed = choice === 'completed';
    return this.run(
      () => this.api.checkIn(batch, completed),
      completed
        ? `Batch ${batch.name} is completed.`
        : `Batch ${batch.name} is returned and pending again.`,
    );
  }

  async assign(batch: ReviewBatchResource): Promise<ReviewBatchResource | null> {
    const ref = this.dialogs.open<string, AssignDialogData>(AssignBatchDialog, {
      data: {
        batchName: batch.name,
        currentId: batch.status === 'checkedOut' ? (batch.assignee?.userId ?? null) : null,
        candidates: () => this.api.candidates(batch.batchSetId),
      },
      injector: this.injector,
    });
    const assigneeId = await firstValueFrom(ref.closed);
    if (!assigneeId) return null;
    return this.run(() => this.api.assign(batch, assigneeId), `Batch ${batch.name} is assigned.`);
  }

  /** A manager makes the batch pending again (the reviewer's coding stays). */
  async release(batch: ReviewBatchResource): Promise<ReviewBatchResource | null> {
    const confirmed = await this.dialogs.confirm({
      title: `Release ${batch.name}?`,
      message:
        'The batch becomes pending, so any reviewer can check it out. Coding already saved stays as it is.',
      confirmLabel: 'Release batch',
    });
    if (!confirmed) return null;
    return this.run(
      () => this.api.assign(batch, null),
      `Batch ${batch.name} is released and pending.`,
    );
  }

  private async run(
    action: () => Promise<ReviewBatchResource>,
    success: string,
  ): Promise<ReviewBatchResource | null> {
    try {
      const batch = await action();
      this.toasts.show(success, { tone: 'success' });
      return batch;
    } catch (e) {
      this.toasts.show(
        batchErrorText(toApiError(e)) ?? 'The batch could not be changed. Try again.',
        {
          tone: 'error',
        },
      );
      return null;
    }
  }
}
