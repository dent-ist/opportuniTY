import { ChangeDetectionStrategy, Component, booleanAttribute, input } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';

/**
 * Styles a native `<button>` or `<a>` (keyboard, focus and semantics stay native).
 * `busy` keeps the button focusable and announces `aria-busy`; the click handler must ignore repeats.
 *
 * Keyboard: Enter / Space activate (native).
 */
@Component({
  selector: 'button[oppButton], a[oppButton]',
  template: `<ng-content />`,
  styleUrl: './button.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'opp-button',
    '[class]': `'opp-button--' + variant()`,
    '[attr.aria-busy]': 'busy() || null',
  },
})
export class Button {
  readonly variant = input<ButtonVariant, ButtonVariant | ''>('secondary', {
    alias: 'oppButton',
    transform: (v) => v || 'secondary',
  });
  readonly busy = input(false, { transform: booleanAttribute });
}

/**
 * Square button that shows only an icon. `label` is required and becomes the accessible name and the
 * native tooltip, so the purpose is available to every user (WCAG 1.1.1, 2.5.3).
 */
@Component({
  selector: 'button[oppIconButton]',
  template: `<ng-content />`,
  styleUrl: './button.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'opp-button opp-button--icon',
    '[class]': `'opp-button--' + variant()`,
    '[attr.aria-label]': 'label()',
    '[attr.title]': 'label()',
  },
})
export class IconButton {
  readonly label = input.required<string>();
  readonly variant = input<ButtonVariant, ButtonVariant | ''>('ghost', {
    alias: 'oppIconButton',
    transform: (v) => v || 'ghost',
  });
}
