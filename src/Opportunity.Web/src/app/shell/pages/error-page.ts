import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { UserFacingError, describeError } from '../../core/api/problem-details';
import { Button, Icon } from '../../ui';
import { SHELL_PATHS, ShellErrors } from '../navigation';

const UNKNOWN: UserFacingError = {
  title: 'Something went wrong',
  detail: 'The page could not be loaded. Try again; if it keeps failing, contact support.',
  retryable: true,
};

/**
 * A page could not be loaded for a reason other than access (server error, network, rate limit). Plain
 * language, the support reference, and a retry of the original URL (ADR-018 §5).
 */
@Component({
  selector: 'opp-error-page',
  imports: [Button, Icon, RouterLink],
  template: `<opp-icon class="page__icon" name="error" />
    <h1 class="page__title">{{ view().title }}</h1>
    <p class="page__lead">{{ view().detail }}</p>
    @if (view().reference; as reference) {
      <p class="page__reference">
        Reference: <code>{{ reference }}</code>
      </p>
    }
    <div class="page__actions">
      @if (view().retryable) {
        <button type="button" oppButton="primary" (click)="retry()">Try again</button>
      }
      <a oppButton="secondary" [routerLink]="workspaces">Go to workspaces</a>
    </div>`,
  styleUrl: './page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'page--centered' },
})
export class ErrorPage {
  private readonly errors = inject(ShellErrors);
  private readonly router = inject(Router);
  protected readonly workspaces = SHELL_PATHS.workspaces;
  protected readonly view = computed(() => {
    const last = this.errors.last();
    return last ? describeError(last.error) : UNKNOWN;
  });

  protected retry(): void {
    void this.router.navigateByUrl(this.errors.last()?.url ?? '/', {
      onSameUrlNavigation: 'reload',
    });
  }
}
