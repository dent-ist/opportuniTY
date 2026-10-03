import { ChangeDetectionStrategy, Component, booleanAttribute, inject, input } from '@angular/core';
import { SessionService } from '../../core/session/session';
import { Button } from '../../ui';
import { PRODUCT_NAME } from '../navigation';

/** Start of the BFF sign-in (code + PKCE happens server-side; the SPA never sees a token). */
@Component({
  selector: 'opp-sign-in',
  imports: [Button],
  template: `<h1 class="page__title">Sign in to {{ productName }}</h1>
    @if (signedOut()) {
      <p class="page__notice" role="status">You have signed out.</p>
    }
    <p class="page__lead">Continue with your organization's account.</p>
    <button type="button" oppButton="primary" class="page__wide" (click)="signIn()">
      Sign in
    </button>`,
  styleUrl: './page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInPage {
  /** Query parameters, bound by the router. */
  readonly returnUrl = input<string>();
  readonly signedOut = input(false, { transform: booleanAttribute });

  protected readonly productName = PRODUCT_NAME;
  private readonly session = inject(SessionService);

  protected signIn(): void {
    this.session.login(this.returnUrl() || '/');
  }
}
