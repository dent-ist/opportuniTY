import { _IdGenerator } from '@angular/cdk/a11y';
import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { Injectable, Injector, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ConfirmDialog } from './confirm-dialog';
import { DIALOG_TITLE_ID } from './dialog-layout';

export interface OpenDialogOptions<D> {
  data?: D;
  /** `alertdialog` for confirmations that interrupt the workflow. */
  role?: 'dialog' | 'alertdialog';
  width?: string;
  /** CSS selector of the element to focus first; default: first tabbable element. */
  autoFocus?: string;
  /** Prevent closing with Escape or a backdrop click (only while a request is in flight). */
  disableClose?: boolean;
  /** Injector of the opening view, so the dialog sees services provided by a feature component. */
  injector?: Injector;
  /** Where focus goes on close; default: the element that had focus when the dialog opened. */
  restoreFocus?: boolean | string | HTMLElement;
}

/**
 * Modal dialogs on the CDK Dialog: focus is trapped inside, moved in on open and restored to the
 * trigger on close; Escape closes; the page behind is inert to assistive technology (aria-modal).
 */
@Injectable({ providedIn: 'root' })
export class DialogService {
  private readonly dialog = inject(Dialog);
  private readonly ids = inject(_IdGenerator);

  open<R = unknown, D = unknown, C = unknown>(
    component: ComponentType<C>,
    options: OpenDialogOptions<D> = {},
  ): DialogRef<R, C> {
    const titleId = this.ids.getId('opp-dialog-title-');
    return this.dialog.open<R, D, C>(component, {
      data: options.data,
      role: options.role ?? 'dialog',
      width: options.width ?? '32rem',
      ariaModal: true,
      ariaLabelledBy: titleId,
      autoFocus: options.autoFocus ?? 'first-tabbable',
      restoreFocus: options.restoreFocus ?? true,
      injector: options.injector,
      closeOnNavigation: true,
      disableClose: options.disableClose ?? false,
      hasBackdrop: true,
      panelClass: 'opp-dialog-pane',
      backdropClass: 'opp-dialog-backdrop',
      providers: [{ provide: DIALOG_TITLE_ID, useValue: titleId }],
    });
  }

  /** Resolves `true` only when the user confirms; Escape, Cancel and close resolve `false`. */
  async confirm(options: ConfirmOptions): Promise<boolean> {
    const ref = this.open<boolean, ConfirmOptions>(ConfirmDialog, {
      data: options,
      role: 'alertdialog',
      autoFocus: options.typedConfirmation ? 'input' : '[data-autofocus]',
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel: string;
  cancelLabel?: string;
  tone?: 'primary' | 'danger';
  /**
   * Text the user must type before confirming (Q-34: bulk actions over 10,000 documents or any
   * security-affecting field).
   */
  typedConfirmation?: string;
}
