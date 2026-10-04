import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { _IdGenerator } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Announcer, Button, Checkbox, DialogLayout, TextField } from '../../../ui';
import { FilterSpec, FilterValue, filterProblem } from './grid-filters';

export interface FilterDialogData {
  readonly spec: FilterSpec;
  readonly value: FilterValue | null;
}

/** Closed with a result: the new value (null clears the filter). Cancel closes without one. */
export interface FilterDialogResult {
  readonly value: FilterValue | null;
}

type Mode = 'value' | 'has' | 'empty';

/**
 * Edits one column's filter (#191): the condition (a value, "has a value", "is empty") and, for a value, the
 * text, the date or number range (either end may stay open) or the choices (any of them). Enter applies.
 */
@Component({
  selector: 'opp-filter-dialog',
  imports: [Button, Checkbox, DialogLayout, TextField],
  template: `
    <opp-dialog-layout [title]="spec.label + ' filter'">
      <div class="fd" (keydown.enter)="onEnter($event)">
        <fieldset class="fd__modes">
          <legend class="fd__legend">Show documents whose {{ spec.label }}</legend>
          @for (m of modes; track m.mode) {
            <label class="fd__mode">
              <input
                type="radio"
                [name]="groupName"
                [value]="m.mode"
                [checked]="mode() === m.mode"
                (change)="mode.set(m.mode)"
              />
              {{ m.label }}
            </label>
          }
        </fieldset>
        @if (mode() === 'value') {
          @switch (spec.kind) {
            @case ('text') {
              <opp-text-field
                [label]="spec.contains ? 'Contains' : 'Starts with'"
                hint="Put the text in quotes for an exact match."
                [(value)]="text"
              />
            }
            @case ('choice') {
              <fieldset class="fd__choices">
                <legend class="opp-visually-hidden">Choices</legend>
                @for (name of spec.choices; track name) {
                  <opp-checkbox
                    [checked]="names().has(name)"
                    (checkedChange)="toggle(name, $event)"
                  >
                    {{ name }}
                  </opp-checkbox>
                } @empty {
                  <p class="fd__note">This field has no choices yet.</p>
                }
              </fieldset>
            }
            @default {
              <div class="fd__range">
                <opp-text-field
                  label="From"
                  [type]="spec.kind === 'date' ? 'date' : 'text'"
                  [(value)]="from"
                  [error]="problem() ?? undefined"
                />
                <opp-text-field
                  label="To"
                  [type]="spec.kind === 'date' ? 'date' : 'text'"
                  [(value)]="to"
                />
              </div>
              <p class="fd__note">{{ rangeHint }}</p>
            }
          }
        }
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="close(null)">Clear filter</button>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="button" oppButton="primary" (click)="apply()">Apply</button>
      </ng-container>
    </opp-dialog-layout>
  `,
  styleUrl: './filter-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FilterDialog {
  protected readonly ref = inject<DialogRef<FilterDialogResult>>(DialogRef);
  private readonly data = inject<FilterDialogData>(DIALOG_DATA);
  protected readonly spec = this.data.spec;
  protected readonly groupName = inject(_IdGenerator).getId('opp-filter-mode-');

  private readonly initial = this.data.value;
  protected readonly mode = signal<Mode>(
    this.initial?.op === 'has' || this.initial?.op === 'empty' ? this.initial.op : 'value',
  );
  protected readonly text = signal(this.initial?.op === 'text' ? this.initial.text : '');
  protected readonly from = signal(this.initial?.op === 'range' ? this.initial.from : '');
  protected readonly to = signal(this.initial?.op === 'range' ? this.initial.to : '');
  protected readonly names = signal<ReadonlySet<string>>(
    new Set(this.initial?.op === 'choices' ? this.initial.names : []),
  );
  private readonly submitted = signal(false);
  private readonly announcer = inject(Announcer);

  protected readonly modes: readonly { mode: Mode; label: string }[] = [
    {
      mode: 'value',
      label: {
        text: 'matches the text',
        date: 'is in a date range',
        number: 'is in a range',
        size: 'is in a size range',
        choice: 'is any of the choices',
        boolean: 'is Yes or No',
      }[this.spec.kind],
    },
    { mode: 'has', label: 'has a value' },
    { mode: 'empty', label: 'is empty' },
  ];

  protected readonly rangeHint =
    this.spec.kind === 'size'
      ? 'Bytes, or with a unit: 10 KB, 2.5 MB, 1 GB. Leave one end empty for an open range.'
      : 'Both ends are included. Leave one end empty for an open range.';

  private readonly value = computed<FilterValue | null>(() => {
    switch (this.mode()) {
      case 'has':
        return { op: 'has' };
      case 'empty':
        return { op: 'empty' };
    }
    switch (this.spec.kind) {
      case 'text':
        return this.text().trim() ? { op: 'text', text: this.text() } : null;
      case 'choice': {
        const names = this.spec.choices.filter((n) => this.names().has(n));
        return names.length ? { op: 'choices', names } : null;
      }
      default:
        return this.from().trim() || this.to().trim()
          ? { op: 'range', from: this.from(), to: this.to() }
          : null;
    }
  });

  protected readonly problem = computed(() => {
    const v = this.value();
    return this.submitted() && v ? filterProblem(this.spec, v) : null;
  });

  protected toggle(name: string, on: boolean): void {
    const next = new Set(this.names());
    if (on) next.add(name);
    else next.delete(name);
    this.names.set(next);
  }

  protected onEnter(event: Event): void {
    const target = event.target as HTMLElement;
    if (target instanceof HTMLInputElement && target.type !== 'checkbox') {
      event.preventDefault();
      this.apply();
    }
  }

  protected apply(): void {
    this.submitted.set(true);
    const v = this.value();
    const problem = v && filterProblem(this.spec, v);
    if (problem) {
      this.announcer.announce(problem, { politeness: 'assertive' });
      return;
    }
    this.close(v);
  }

  protected close(value: FilterValue | null): void {
    this.ref.close({ value });
  }
}
