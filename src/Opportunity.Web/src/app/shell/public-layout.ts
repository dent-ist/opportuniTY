import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Brand } from './brand';
import { SkipLink } from './skip-link';

/** Layout for pages shown without a session: sign-in and session errors. */
@Component({
  selector: 'opp-public-layout',
  imports: [RouterOutlet, Brand, SkipLink],
  template: `<opp-skip-link />
    <header class="public__header">
      <opp-brand />
    </header>
    <main id="main" class="public__main" tabindex="-1">
      <div class="public__card"><router-outlet /></div>
    </main>`,
  styleUrl: './public-layout.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'public' },
})
export class PublicLayout {}
