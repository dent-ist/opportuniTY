import { _IdGenerator } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DialogService, Icon, IconButton } from '../../../ui';
import { FilterDialog, FilterDialogData, FilterDialogResult } from './filter-dialog';
import { FilterSpec, FilterValue, describeFilter } from './grid-filters';

/** A new filter value; `immediate` skips the typing debounce (Enter, a choice, a dialog). */
export interface FilterChange {
  readonly value: FilterValue | null;
  readonly immediate: boolean;
}

/** Wait this long after the last keystroke before filtering. */
export const FILTER_DEBOUNCE_MS = 400;

/**
 * One column's control in the filter row (#191): a text box for text fields, Any / Yes / No for Yes/No fields,
 * and for dates, numbers, sizes and choices a button that shows the condition and edits it in a dialog. Every
 * type also offers "has a value" and "is empty" (text: the options button). Controls carry `data-filter-stop`
 * for the row's arrow-key navigation; a server message for the filter is shown under it.
 */
@Component({
  selector: 'opp-grid-filter',
  imports: [Icon, IconButton],
  template: `
    @let s = spec();
    @switch (s.kind) {
      @case ('text') {
        <div class="filter__box">
          <input
            data-filter-stop
            class="filter__control filter__input"
            type="text"
            autocomplete="off"
            [attr.aria-label]="'Filter ' + s.label"
            [attr.aria-invalid]="error() ? true : null"
            [attr.aria-describedby]="error() ? errorId : hintId"
            [placeholder]="s.contains ? 'Contains' : 'Starts with'"
            [value]="presence() ?? text()"
            [readOnly]="presence() !== null"
            [class.is-presence]="presence() !== null"
            (input)="onInput($event)"
            (keydown.enter)="onEnter()"
            (keydown.alt.arrowdown)="$event.preventDefault(); edit()"
          />
          <button
            data-filter-stop
            type="button"
            oppIconButton
            class="filter__more"
            [label]="s.label + ' filter options'"
            aria-haspopup="dialog"
            (click)="edit()"
          >
            <opp-icon name="chevron-down" />
          </button>
        </div>
        <span class="opp-visually-hidden" [id]="hintId">{{ textHint() }}</span>
      }
      @case ('boolean') {
        <select
          data-filter-stop
          class="filter__control"
          [attr.aria-label]="'Filter ' + s.label"
          [attr.aria-invalid]="error() ? true : null"
          [attr.aria-describedby]="error() ? errorId : null"
          (change)="onBoolean($event)"
        >
          @for (o of BOOLEAN; track o.value) {
            <option [value]="o.value" [selected]="o.value === booleanValue()">{{ o.label }}</option>
          }
        </select>
      }
      @default {
        <button
          data-filter-stop
          type="button"
          class="filter__control filter__trigger"
          aria-haspopup="dialog"
          [class.is-set]="!!value()"
          [attr.aria-label]="'Filter ' + s.label + ': ' + summary()"
          [attr.aria-invalid]="error() ? true : null"
          [attr.aria-describedby]="error() ? errorId : null"
          (click)="edit()"
        >
          <span class="filter__summary">{{ summary() }}</span>
          <opp-icon name="filter" />
        </button>
      }
    }
    @if (error(); as message) {
      <p class="filter__error" [id]="errorId">
        <opp-icon name="error" /><span class="opp-visually-hidden">Error:</span>{{ message }}
      </p>
    }
  `,
  styleUrl: './grid-filter.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[attr.data-filter-field]': 'spec().queryName',
    '(keydown.escape)': 'clear($event)',
  },
})
export class GridFilter {
  readonly spec = input.required<FilterSpec>();
  readonly value = input<FilterValue | null>(null);
  /** The server's message for this filter (query errors), or null. */
  readonly error = input<string | null>(null);
  readonly filterChange = output<FilterChange>();

  private readonly dialogs = inject(DialogService);
  protected readonly errorId = inject(_IdGenerator).getId('opp-filter-error-');
  protected readonly hintId = `${this.errorId}-hint`;
  private timer: ReturnType<typeof setTimeout> | undefined;

  /** What the text box shows; follows the applied value, and the user's typing until it is applied. */
  protected readonly text = linkedSignal(() => {
    const v = this.value();
    return v?.op === 'text' ? v.text : '';
  });
  protected readonly presence = computed(() => {
    const op = this.value()?.op;
    return op === 'has' ? 'Has a value' : op === 'empty' ? 'Is empty' : null;
  });
  protected readonly summary = computed(() => {
    const v = this.value();
    return v ? describeFilter(this.spec(), v) : 'Any';
  });
  protected readonly booleanValue = computed(() => {
    const v = this.value();
    return v?.op === 'boolean' ? String(v.value) : (v?.op ?? 'any');
  });
  protected readonly textHint = computed(
    () =>
      `${this.spec().contains ? 'Contains' : 'Starts with'} the text; put it in quotes for an exact match. Alt+Down for more options, Escape clears.`,
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected onInput(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.text.set(text);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.emitText(false), FILTER_DEBOUNCE_MS);
  }

  protected onEnter(): void {
    if (this.presence() === null) this.emitText(true);
  }

  private emitText(immediate: boolean): void {
    clearTimeout(this.timer);
    const text = this.text();
    this.filterChange.emit({ value: text.trim() ? { op: 'text', text } : null, immediate });
  }

  protected onBoolean(event: Event): void {
    const v = (event.target as HTMLSelectElement).value;
    const value: FilterValue | null =
      v === 'true' || v === 'false'
        ? { op: 'boolean', value: v === 'true' }
        : v === 'has' || v === 'empty'
          ? { op: v }
          : null;
    this.filterChange.emit({ value, immediate: true });
  }

  /** Opens the filter dialog (the options button, the trigger, or Alt+Down in the text box). */
  async edit(): Promise<void> {
    clearTimeout(this.timer);
    const pending = this.text();
    const current = this.value();
    const ref = this.dialogs.open<FilterDialogResult, FilterDialogData>(FilterDialog, {
      data: {
        spec: this.spec(),
        value:
          this.spec().kind === 'text' && pending.trim() ? { op: 'text', text: pending } : current,
      },
      width: '26rem',
    });
    const result = await firstValueFrom(ref.closed);
    if (result) this.filterChange.emit({ value: result.value, immediate: true });
    // Cancelled: typing in the text box that was not applied yet is applied now.
    else if (this.presence() === null && (current?.op === 'text' ? current.text : '') !== pending)
      this.emitText(true);
  }

  /** Escape: clears this filter, typing that was not applied yet included. */
  protected clear(event: Event): void {
    if (!this.value() && !this.text()) return;
    event.preventDefault();
    clearTimeout(this.timer);
    this.text.set('');
    this.filterChange.emit({ value: null, immediate: true });
  }

  protected readonly BOOLEAN = [
    { value: 'any', label: 'Any' },
    { value: 'true', label: 'Yes' },
    { value: 'false', label: 'No' },
    { value: 'has', label: 'Has a value' },
    { value: 'empty', label: 'Is empty' },
  ];
}
