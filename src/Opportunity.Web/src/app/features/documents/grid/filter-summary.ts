import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  output,
} from '@angular/core';
import { Button, Icon } from '../../../ui';

export interface ActiveFilter {
  readonly queryName: string;
  readonly label: string;
  readonly description: string;
  readonly error: string | null;
}

/**
 * "Filters: File Name, Document Date · Clear all" above the document list (#191): every active filter as a
 * removable chip, shown whether or not the filter row is open, so a filter is never applied out of sight.
 * With the row closed, a filter the server rejected shows its message here.
 */
@Component({
  selector: 'opp-filter-summary',
  imports: [Button, Icon],
  template: `
    <div class="summary" role="group" aria-label="Active filters">
      <span class="summary__title">Filters:</span>
      <ul class="summary__chips">
        @for (f of filters(); track f.queryName) {
          <li>
            <button
              type="button"
              class="chip"
              [class.is-error]="!!f.error"
              [attr.aria-label]="'Remove filter: ' + f.label + ' ' + f.description"
              [title]="f.label + ' ' + f.description"
              (click)="removed.emit(f.queryName)"
            >
              @if (f.error) {
                <opp-icon name="error" />
              }
              <span class="chip__label">{{ f.label }}</span>
              <span class="chip__value">{{ f.description }}</span>
              <opp-icon name="close" />
            </button>
          </li>
        }
      </ul>
      <span class="summary__sep" aria-hidden="true">·</span>
      <button type="button" oppButton="ghost" (click)="cleared.emit()">Clear all</button>
    </div>
    @if (showErrors()) {
      @for (f of filters(); track f.queryName) {
        @if (f.error) {
          <p class="summary__error">
            <opp-icon name="error" />
            <span><span class="opp-visually-hidden">Error:</span>{{ f.label }}: {{ f.error }}</span>
          </p>
        }
      }
    }
  `,
  styleUrl: './filter-summary.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FilterSummary {
  readonly filters = input.required<readonly ActiveFilter[]>();
  /** Show server messages here (the filter row, where they normally appear, is closed). */
  readonly showErrors = input(false);
  readonly removed = output<string>();
  readonly cleared = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Focus for after a chip was removed: the next chip, else Clear all. */
  focusChip(index: number): void {
    const chips = this.host.nativeElement.querySelectorAll<HTMLElement>('.chip');
    (chips[Math.min(index, chips.length - 1)] ?? null)?.focus();
  }
}
