import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { ReviewBatchUserResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { Button, DialogLayout, ErrorState, LoadingState, Select, SelectOption } from '../../ui';

export type CheckInChoice = 'completed' | 'returned';

export interface CheckInDialogData {
  readonly batchName: string;
}

/**
 * Check in a batch (familiarity guide §5.3): done (Completed), or returned unfinished so another reviewer can take it
 * (Pending again). Completed is focused; Escape and Cancel keep the batch checked out.
 */
@Component({
  selector: 'opp-check-in-dialog',
  imports: [Button, DialogLayout],
  template: `<opp-dialog-layout [title]="'Check in ' + data.batchName">
    <p>
      Mark the batch completed when every document is reviewed. Return it unfinished to make it
      available to another reviewer.
    </p>
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
      <button type="button" oppButton="secondary" (click)="ref.close('returned')">
        Return unfinished
      </button>
      <button type="button" oppButton="primary" data-autofocus (click)="ref.close('completed')">
        Mark completed
      </button>
    </ng-container>
  </opp-dialog-layout>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CheckInDialog {
  protected readonly ref = inject<DialogRef<CheckInChoice>>(DialogRef);
  protected readonly data = inject<CheckInDialogData>(DIALOG_DATA);
}

export interface AssignDialogData {
  readonly batchName: string;
  /** The reviewer holding the batch now, preselected. */
  readonly currentId: string | null;
  readonly candidates: () => Promise<readonly ReviewBatchUserResource[]>;
}

/** Assign (or reassign) a batch to a reviewer who may code; closes with the chosen user ID. */
@Component({
  selector: 'opp-assign-batch-dialog',
  imports: [Button, DialogLayout, ErrorState, LoadingState, Select],
  template: `<opp-dialog-layout [title]="'Assign ' + data.batchName">
    @if (error(); as e) {
      <opp-error-state [error]="e" />
    } @else if (options() === null) {
      <opp-loading-state label="Loading reviewers…" />
    } @else if (options()!.length === 0) {
      <p>No member who may code can take this batch.</p>
    } @else {
      <p>The batch is checked out to the reviewer you choose, also when it was completed.</p>
      <opp-select
        label="Reviewer"
        placeholder="Choose a reviewer"
        [options]="options()!"
        [value]="chosen()"
        (valueChange)="chosen.set($event)"
      />
    }
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
      <button
        type="button"
        oppButton="primary"
        [disabled]="!chosen() || chosen() === data.currentId"
        (click)="ref.close(chosen())"
      >
        Assign
      </button>
    </ng-container>
  </opp-dialog-layout>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssignBatchDialog {
  protected readonly ref = inject<DialogRef<string>>(DialogRef);
  protected readonly data = inject<AssignDialogData>(DIALOG_DATA);
  private readonly candidates = signal<readonly ReviewBatchUserResource[] | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly chosen = signal(this.data.currentId ?? '');
  protected readonly options = computed<SelectOption[] | null>(() => {
    const list = this.candidates();
    return list
      ? list.map((c) => ({ value: c.userId, label: c.displayName ?? 'Unknown user' }))
      : null;
  });

  constructor() {
    this.data.candidates().then(
      (list) => this.candidates.set(list),
      (e: unknown) => this.error.set(toApiError(e)),
    );
  }
}
