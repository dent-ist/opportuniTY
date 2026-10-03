import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, InjectionToken, inject, input } from '@angular/core';
import { IconButton } from '../button/button';
import { Icon } from '../icon/icon';

/** Id of the dialog's heading, generated per dialog so the container is labelled by it. */
export const DIALOG_TITLE_ID = new InjectionToken<string>('DIALOG_TITLE_ID');

/**
 * Standard dialog chrome: heading (labels the dialog), close button, scrollable body and an actions row.
 * Project actions with the `dialogActions` attribute.
 */
@Component({
  selector: 'opp-dialog-layout',
  imports: [IconButton, Icon],
  template: `
    <header class="dialog__header">
      <h2 class="dialog__title" [id]="titleId">{{ title() }}</h2>
      <button type="button" oppIconButton label="Close dialog" (click)="ref.close()">
        <opp-icon name="close" />
      </button>
    </header>
    <div class="dialog__body"><ng-content /></div>
    <footer class="dialog__actions"><ng-content select="[dialogActions]" /></footer>
  `,
  styleUrl: './dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DialogLayout {
  readonly title = input.required<string>();
  protected readonly ref = inject(DialogRef);
  protected readonly titleId = inject(DIALOG_TITLE_ID, { optional: true }) ?? undefined;
}
