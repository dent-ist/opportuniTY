import { ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';

/**
 * First focusable element of every layout (WCAG 2.4.1). Moves focus to `#main` itself: a plain `#main`
 * href would resolve against `<base href>` and trigger router navigation.
 */
@Component({
  selector: 'opp-skip-link',
  template: `<a class="skip-link" href="#main" (click)="skip($event)">Skip to main content</a>`,
  styleUrl: './skip-link.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SkipLink {
  private readonly document = inject(DOCUMENT);

  protected skip(event: Event): void {
    event.preventDefault();
    const main = this.document.getElementById('main');
    main?.focus();
    main?.scrollIntoView?.({ block: 'start' });
  }
}
