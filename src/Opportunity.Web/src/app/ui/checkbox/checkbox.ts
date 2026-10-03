import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  model,
  output,
  viewChild,
} from '@angular/core';
import type { FormCheckboxControl } from '@angular/forms/signals';
import { Icon } from '../icon/icon';

/**
 * Native checkbox with a 24 px hit area. The projected content is the visible label; use `ariaLabel`
 * when there is none (e.g. row selection in a grid: "Select DOC-000123"). `indeterminate` supports the
 * grid's "some rows selected" header state.
 *
 * Keyboard: Space toggles (native).
 */
@Component({
  selector: 'opp-checkbox',
  imports: [Icon],
  templateUrl: './checkbox.html',
  styleUrl: './checkbox.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Checkbox implements FormCheckboxControl {
  readonly checked = model(false);
  readonly indeterminate = input(false);
  readonly disabled = input(false);
  readonly required = input(false);
  readonly invalid = input(false);
  readonly ariaLabel = input<string>();
  readonly touch = output<void>();

  private readonly inputEl = viewChild.required<ElementRef<HTMLInputElement>>('input');

  constructor() {
    effect(() => {
      this.inputEl().nativeElement.indeterminate = this.indeterminate() && !this.checked();
    });
  }

  focus(options?: FocusOptions): void {
    this.inputEl().nativeElement.focus(options);
  }

  protected onChange(event: Event): void {
    this.checked.set((event.target as HTMLInputElement).checked);
  }
}
