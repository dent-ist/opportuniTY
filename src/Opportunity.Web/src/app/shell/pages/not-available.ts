import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Button, Icon } from '../../ui';
import { SHELL_PATHS } from '../navigation';

/**
 * The one page for "does not exist" and "no access" (Q-13, ADR-019 §2.3): the wording never reveals
 * which, so a deep link cannot be used to probe for workspaces or documents.
 */
@Component({
  selector: 'opp-not-available',
  imports: [Button, Icon, RouterLink],
  template: `<opp-icon class="page__icon" name="lock" />
    <h1 class="page__title">Not available</h1>
    <p class="page__lead">
      This page does not exist, or you do not have access to it. If you expected to see it, ask your
      workspace administrator.
    </p>
    <div class="page__actions">
      <a oppButton="primary" [routerLink]="workspaces">Go to workspaces</a>
    </div>`,
  styleUrl: './page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'page--centered' },
})
export class NotAvailablePage {
  protected readonly workspaces = SHELL_PATHS.workspaces;
}
