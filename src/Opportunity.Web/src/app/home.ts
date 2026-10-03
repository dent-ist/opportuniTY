import { ChangeDetectionStrategy, Component } from '@angular/core';
import { EmptyState } from './ui';

/** Placeholder start page until the application shell lands (E15-T02). */
@Component({
  selector: 'opp-home',
  imports: [EmptyState],
  template: `<main>
    <h1 class="opp-visually-hidden">opportuniTY</h1>
    <opp-empty-state title="opportuniTY" message="The review workspace is not available yet." />
  </main>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Home {}
