import { _IdGenerator } from '@angular/cdk/a11y';
import { Directive, computed, inject, input } from '@angular/core';

/**
 * Shared label / hint / error plumbing for form controls. Errors are linked with `aria-describedby` and
 * `aria-invalid`; the message is shown in text (never colour alone) and is not a live region: forms
 * announce a summary on submit instead of interrupting typing.
 */
@Directive()
export abstract class FieldBase {
  readonly label = input.required<string>();
  readonly hint = input<string>();
  /** Error text to show. Signal Forms bind `invalid`; the caller passes the message to show. */
  readonly error = input<string>();
  readonly invalid = input(false);
  readonly required = input(false);
  readonly disabled = input(false);

  protected readonly id = inject(_IdGenerator).getId('opp-field-');
  protected readonly hintId = `${this.id}-hint`;
  protected readonly errorId = `${this.id}-error`;
  protected readonly showError = computed(() => !!this.error());
  protected readonly ariaInvalid = computed(() => this.invalid() || this.showError());
  protected readonly describedBy = computed(
    () =>
      [this.hint() ? this.hintId : null, this.showError() ? this.errorId : null]
        .filter(Boolean)
        .join(' ') || null,
  );
}
