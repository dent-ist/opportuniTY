import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Button } from '../button/button';
import { TextField } from '../field/text-field';
import type { ConfirmOptions } from './dialog';
import { DialogLayout } from './dialog-layout';

/** Confirmation used by `DialogService.confirm`. Keyboard: Tab cycles inside; Enter submits; Escape cancels. */
@Component({
  selector: 'opp-confirm-dialog',
  imports: [DialogLayout, Button, TextField],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  protected readonly options = inject<ConfirmOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  protected readonly typed = signal('');
  protected readonly canConfirm = computed(
    () => !this.options.typedConfirmation || this.typed().trim() === this.options.typedConfirmation,
  );

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.canConfirm()) this.ref.close(true);
  }
}
