import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  model,
  output,
  viewChild,
} from '@angular/core';
import type { FormValueControl } from '@angular/forms/signals';
import { FieldBase } from './field';

/**
 * Labelled single-line text input. Works standalone (`[(value)]`) and with Signal Forms (`[formField]`),
 * which bind `value`, `disabled`, `invalid`, `required` and listen to `touch`.
 *
 * Keyboard: native text input.
 */
@Component({
  selector: 'opp-text-field',
  templateUrl: './text-field.html',
  styleUrl: './field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TextField extends FieldBase implements FormValueControl<string> {
  readonly value = model('');
  readonly type = input<
    'text' | 'search' | 'email' | 'password' | 'url' | 'tel' | 'date' | 'datetime-local'
  >('text');
  readonly placeholder = input<string>();
  readonly autocomplete = input<string>('off');
  readonly readonly = input(false);
  readonly touch = output<void>();

  private readonly inputEl = viewChild.required<ElementRef<HTMLInputElement>>('input');

  focus(options?: FocusOptions): void {
    this.inputEl().nativeElement.focus(options);
  }

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);
  }
}
