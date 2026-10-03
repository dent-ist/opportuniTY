import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import type { FormValueControl } from '@angular/forms/signals';
import { FieldBase } from './field';

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

/**
 * Labelled single choice on a native `<select>`: platform keyboard, type-ahead and screen-reader support
 * for free, and the fastest option in dense coding layouts. Multi-choice uses the @angular/aria listbox.
 *
 * Keyboard: native select (arrows, type-ahead, Alt+Down / Space opens the list).
 */
@Component({
  selector: 'opp-select',
  templateUrl: './select.html',
  styleUrl: './field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Select extends FieldBase implements FormValueControl<string> {
  readonly value = model('');
  readonly options = input.required<readonly SelectOption[]>();
  /** Text of the empty first option; omit to force a choice. */
  readonly placeholder = input<string>();
  readonly touch = output<void>();

  protected onChange(event: Event): void {
    this.value.set((event.target as HTMLSelectElement).value);
  }
}
