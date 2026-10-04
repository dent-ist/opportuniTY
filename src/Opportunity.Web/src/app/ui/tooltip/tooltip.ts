import { _IdGenerator } from '@angular/cdk/a11y';
import { DOCUMENT, Directive, ElementRef, effect, inject, input } from '@angular/core';

/** Delay before a tooltip hides after the pointer leaves, so it can move onto the tooltip (WCAG 1.4.13). */
const HIDE_DELAY_MS = 150;

/**
 * A short description shown on hover and keyboard focus, and given to assistive technology as the host's
 * description (`aria-describedby`), so it works for disabled-but-focusable controls (`aria-disabled`) where the
 * native `title` does not. WCAG 1.4.13: it stays while the pointer is over it, and Escape dismisses it.
 * Styles live in src/styles/_tooltip.scss (the element is attached to the page body). No text, no tooltip.
 *
 * <li oppTab value="image" [oppTooltip]="reason">Image</li>
 */
@Directive({
  selector: '[oppTooltip]',
  host: {
    '[attr.aria-describedby]': 'oppTooltip() ? id : null',
    '(mouseenter)': 'show()',
    '(mouseleave)': 'hideSoon()',
    '(focusin)': 'show()',
    '(focusout)': 'hide()',
    '(keydown.escape)': 'dismiss($event)',
  },
})
export class Tooltip {
  readonly oppTooltip = input<string | null | undefined>(null);

  protected readonly id = inject(_IdGenerator).getId('opp-tooltip-');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private tip?: HTMLElement;
  private timer?: ReturnType<typeof setTimeout>;

  constructor() {
    // The description element exists (hidden) for as long as there is text, so `aria-describedby` always resolves.
    effect((onCleanup) => {
      const text = this.oppTooltip();
      if (!text) return;
      const tip = this.document.createElement('div');
      tip.id = this.id;
      tip.className = 'opp-tooltip';
      tip.setAttribute('role', 'tooltip');
      tip.hidden = true;
      tip.textContent = text;
      tip.addEventListener('mouseenter', () => clearTimeout(this.timer));
      tip.addEventListener('mouseleave', () => this.hide());
      this.document.body.appendChild(tip);
      this.tip = tip;
      onCleanup(() => {
        clearTimeout(this.timer);
        tip.remove();
        if (this.tip === tip) this.tip = undefined;
      });
    });
  }

  protected show(): void {
    clearTimeout(this.timer);
    const tip = this.tip;
    if (!tip) return;
    tip.hidden = false;
    const host = this.host.nativeElement.getBoundingClientRect();
    const view = this.document.documentElement;
    const left = Math.max(8, Math.min(host.left, view.clientWidth - tip.offsetWidth - 8));
    const below = host.bottom + 6;
    const top =
      below + tip.offsetHeight > view.clientHeight ? host.top - tip.offsetHeight - 6 : below;
    tip.style.left = `${left}px`;
    tip.style.top = `${Math.max(8, top)}px`;
  }

  protected hideSoon(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.hide(), HIDE_DELAY_MS);
  }

  protected hide(): void {
    clearTimeout(this.timer);
    if (this.tip) this.tip.hidden = true;
  }

  /** Escape closes a visible tooltip without doing anything else (such as leaving Review mode). */
  protected dismiss(event: Event): void {
    if (!this.tip || this.tip.hidden) return;
    event.stopPropagation();
    event.preventDefault();
    this.hide();
  }
}
