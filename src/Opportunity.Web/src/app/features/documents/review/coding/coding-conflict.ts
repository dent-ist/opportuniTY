import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  output,
} from '@angular/core';
import { Button, Icon } from '../../../../ui';

/** A field whose current value differs from the reviewer's. */
export interface CodingDifference {
  readonly label: string;
  readonly theirs: string;
  readonly yours: string;
}

/**
 * A save that met a newer version (412, familiarity guide §3.3): who changed the document and when, the fields
 * whose current value differs from the reviewer's, and the two ways forward. Nothing is overwritten until the
 * reviewer chooses "Overwrite with mine".
 */
@Component({
  selector: 'opp-coding-conflict',
  imports: [Button, Icon],
  template: `<p class="conflict__title" [id]="titleId()">
      <opp-icon name="warning" /><strong>{{ title() }}</strong>
    </p>
    <p class="conflict__text">
      Your changes were not saved. Review the differences, then keep the current coding or apply
      yours on top of it.
    </p>
    @if (differences().length) {
      <table class="conflict__diff">
        <caption class="opp-visually-hidden">
          Differences between the current coding and yours
        </caption>
        <thead>
          <tr>
            <th scope="col">Field</th>
            <th scope="col">Current</th>
            <th scope="col">Yours</th>
          </tr>
        </thead>
        <tbody>
          @for (d of differences(); track d.label) {
            <tr>
              <th scope="row">{{ d.label }}</th>
              <td>{{ d.theirs }}</td>
              <td>{{ d.yours }}</td>
            </tr>
          }
        </tbody>
      </table>
    }
    <div class="conflict__actions">
      <button type="button" oppButton="secondary" (click)="reload.emit()">
        <opp-icon name="refresh" />Reload current coding
      </button>
      <button type="button" oppButton="primary" (click)="overwrite.emit()">
        Overwrite with mine
      </button>
    </div>`,
  styleUrl: './coding-conflict.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'alert',
    tabindex: '-1',
    '[attr.aria-labelledby]': 'titleId()',
  },
})
export class CodingConflict {
  readonly title = input.required<string>();
  readonly differences = input.required<readonly CodingDifference[]>();
  readonly titleId = input.required<string>();
  /** Discard the reviewer's edits and show the current coding. */
  readonly reload = output<void>();
  /** Save the reviewer's changed fields on top of the current version. */
  readonly overwrite = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  focus(): void {
    this.host.focus();
  }
}
