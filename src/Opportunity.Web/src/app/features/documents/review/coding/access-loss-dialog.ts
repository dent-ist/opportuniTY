import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Button, DialogLayout, Icon } from '../../../../ui';

export interface AccessLossData {
  /** The document's Control Number, as the reviewer knows it. */
  readonly controlNumber: string | null;
  /** Labels of the changed fields that affect access (in layout order). */
  readonly fields: readonly string[];
}

/**
 * Saving a change that would hide the document from the reviewer (E16-T08, familiarity guide §3.3): the API refused
 * it until confirmed. Cancel is focused, and Escape cancels; the edits stay unsaved either way until the reviewer
 * confirms. The dialog names the reviewer's own change only, never why or by which rule they would lose access.
 */
@Component({
  selector: 'opp-access-loss-dialog',
  imports: [Button, DialogLayout, Icon],
  template: `<opp-dialog-layout title="Save and lose access?">
    <p class="access-loss__lead">
      <opp-icon name="lock" />
      <span
        >After this save you will no longer have access to
        <strong>{{ data.controlNumber ?? 'this document' }}</strong
        >{{ fieldsText }}.</span
      >
    </p>
    <p>
      It will show as “No longer available” in the list and Review moves on to the next document.
      Only someone who can still see it can change it back.
    </p>
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" data-autofocus (click)="ref.close(false)">
        Cancel
      </button>
      <button type="button" oppButton="danger" (click)="ref.close(true)">
        Save and lose access
      </button>
    </ng-container>
  </opp-dialog-layout>`,
  styleUrl: './access-loss-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccessLossDialog {
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly data = inject<AccessLossData>(DIALOG_DATA);
  protected readonly fieldsText =
    this.data.fields.length === 0
      ? ''
      : `: you changed ${listOf(this.data.fields)}, which ${this.data.fields.length === 1 ? 'affects' : 'affect'} who may see it`;
}

function listOf(items: readonly string[]): string {
  return items.length <= 1
    ? (items[0] ?? '')
    : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}
