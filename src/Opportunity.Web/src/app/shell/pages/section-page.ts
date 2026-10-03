import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EmptyState } from '../../ui';

/**
 * Landing page of a workspace section whose feature has not shipped yet. The navigation, permissions and
 * URL are final; the feature replaces this component on its route.
 */
@Component({
  selector: 'opp-section-page',
  imports: [EmptyState],
  template: `<h1 class="page__title">{{ section() }}</h1>
    <opp-empty-state
      title="Not available in this version"
      [message]="'The ' + section() + ' page is not part of this release yet.'"
    />`,
  styleUrl: './page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'page--content' },
})
export class SectionPage {
  /** Route data, bound by the router. */
  readonly section = input.required<string>();
}
