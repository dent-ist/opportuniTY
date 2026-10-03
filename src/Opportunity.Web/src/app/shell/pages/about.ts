import { ChangeDetectionStrategy, Component } from '@angular/core';
import { PRODUCT_NAME } from '../navigation';

/**
 * About page: licence notice and the legal disclaimer (Q-25). No third-party names or marks (Q-47, Q-51).
 * Keep the disclaimer in step with the README.
 */
@Component({
  selector: 'opp-about',
  template: `<h1 class="page__title">About {{ productName }}</h1>
    <p class="page__lead">
      {{ productName }} is a self-hosted platform for reviewing, coding and producing documents in
      legal matters.
    </p>
    <section class="page__section" aria-labelledby="about-licence">
      <h2 id="about-licence">Licence</h2>
      <p>
        {{ productName }} is open-source software released under the MIT License. Copyright © 2026
        Nice Bug.
      </p>
      <p>
        The software is provided "as is", without warranty of any kind, express or implied. The full
        licence text is distributed with the software.
      </p>
      <p>Components from other open-source projects remain under their own licences.</p>
    </section>
    <section class="page__section" aria-labelledby="about-disclaimer">
      <h2 id="about-disclaimer">Legal disclaimer</h2>
      <p>
        {{ productName }} is software, not legal advice. It does not guarantee the defensibility of
        any review, privilege determination, redaction or production. Users and their counsel remain
        responsible for verifying all outputs and for compliance with applicable rules, court
        orders, protective orders and ESI agreements.
      </p>
    </section>`,
  styleUrl: './page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'page--content' },
})
export class AboutPage {
  protected readonly productName = PRODUCT_NAME;
}
