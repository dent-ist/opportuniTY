import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SessionService } from '../core/session/session';
import { Button, DialogLayout } from '../ui';

/** Shown when any API call returns 401 after sign-in (idle/absolute timeout, back-channel logout). */
@Component({
  selector: 'opp-session-ended-dialog',
  imports: [DialogLayout, Button],
  template: `<opp-dialog-layout title="Your session has ended">
    <p class="session-ended__message">
      For your security you were signed out. Sign in again to continue where you left off. Changes
      that were not saved before the session ended were not kept.
    </p>
    <ng-container dialogActions>
      <button type="button" oppButton="primary" data-autofocus (click)="signIn()">
        Sign in again
      </button>
    </ng-container>
  </opp-dialog-layout>`,
  styleUrl: './session-ended-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SessionEndedDialog {
  private readonly session = inject(SessionService);

  protected signIn(): void {
    this.session.login();
  }
}
