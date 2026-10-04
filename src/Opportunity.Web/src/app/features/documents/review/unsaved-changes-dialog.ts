import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Button, DialogLayout } from '../../../ui';

export type UnsavedChoice = 'save' | 'discard' | 'cancel';

/**
 * Leaving a document with unsaved coding (familiarity guide §3.2): Save, Discard or Cancel, with Save focused.
 * Escape and the close button cancel.
 */
@Component({
  selector: 'opp-unsaved-changes-dialog',
  imports: [Button, DialogLayout],
  template: `<opp-dialog-layout title="Unsaved coding">
    <p>This document has coding changes that are not saved.</p>
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" (click)="ref.close('cancel')">Cancel</button>
      <button type="button" oppButton="secondary" (click)="ref.close('discard')">Discard</button>
      <button type="button" oppButton="primary" data-autofocus (click)="ref.close('save')">
        Save
      </button>
    </ng-container>
  </opp-dialog-layout>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UnsavedChangesDialog {
  protected readonly ref = inject<DialogRef<UnsavedChoice>>(DialogRef);
}
