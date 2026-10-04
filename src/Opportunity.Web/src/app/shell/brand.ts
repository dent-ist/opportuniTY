import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The product logo: the approved mark (assets/brand, served from public/brand) and the wordmark as text. The mark is
 * decorative (empty alt) because the name next to it is the accessible label.
 */
@Component({
  selector: 'opp-brand',
  template: `<img
      class="brand__mark"
      src="/brand/opportunity-mark-32.png"
      srcset="/brand/opportunity-mark-32.png 1x, /brand/opportunity-mark-64.png 2x"
      width="28"
      height="28"
      alt=""
    />
    <span class="brand__name">opportuni<span class="brand__ty">TY</span></span>`,
  styleUrl: './brand.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'brand' },
})
export class Brand {}
