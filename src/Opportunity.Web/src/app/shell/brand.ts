import { ChangeDetectionStrategy, Component } from '@angular/core';

/** The product wordmark with our own mark (two offset pages). Decorative mark; the name is text. */
@Component({
  selector: 'opp-brand',
  template: `<svg class="brand__mark" viewBox="0 0 24 24" focusable="false" aria-hidden="true">
      <rect x="3.5" y="5.5" width="11" height="15" rx="1.5" />
      <path d="M8.5 3.5h9a2 2 0 012 2v12" />
      <path d="M6.5 10h5M6.5 13h5M6.5 16h3" />
    </svg>
    <span class="brand__name">opportuni<span class="brand__ty">TY</span></span>`,
  styleUrl: './brand.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'brand' },
})
export class Brand {}
