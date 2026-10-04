import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { DocumentResource } from '../../../../core/api/generated/models';
import { Icon, Tooltip } from '../../../../ui';

interface Row {
  readonly id: string;
  readonly label: string;
  readonly value: string;
  /** The value as imported, when it differs from the display value (dates in the display time zone). */
  readonly raw: string | null;
  readonly type: string;
}

/**
 * Metadata mode (E16-T04, familiarity guide §3.2): every field the reviewer may see, from
 * `GET …/documents/{id}` (already loaded with the document, so no extra request): name, display value (instants in
 * the workspace display time zone) with the raw imported string in a tooltip (Q-28), and the field type. A filter
 * narrows long field lists.
 */
@Component({
  selector: 'opp-viewer-metadata',
  imports: [Icon, Tooltip],
  template: `<div class="viewer-tools">
      <span class="viewer-tools__search">
        <opp-icon name="filter" />
        <input
          type="search"
          class="viewer-tools__input"
          aria-label="Filter fields"
          placeholder="Filter fields"
          autocomplete="off"
          (input)="filter.set($any($event.target).value)"
        />
      </span>
      <span class="viewer-tools__count">{{ countText() }}</span>
      <span class="metadata__zone">Times in {{ metadata().displayTimeZone }}</span>
    </div>
    <div class="metadata__scroll" tabindex="0" role="group" [attr.aria-label]="caption()">
      <table class="metadata" [attr.data-viewer-content]="metadata().documentId">
        <caption class="opp-visually-hidden">
          {{
            caption()
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">Field</th>
            <th scope="col">Value</th>
            <th scope="col">Type</th>
          </tr>
        </thead>
        <tbody>
          @for (row of visible(); track row.id) {
            <tr>
              <th scope="row">{{ row.label }}</th>
              <td>
                @if (row.raw !== null) {
                  <span
                    class="metadata__value metadata__value--raw"
                    tabindex="0"
                    [oppTooltip]="'Imported value: ' + row.raw"
                    >{{ row.value }}</span
                  >
                } @else {
                  <span class="metadata__value" [class.metadata__empty]="!row.value">{{
                    row.value || '—'
                  }}</span>
                }
              </td>
              <td class="metadata__type">{{ row.type }}</td>
            </tr>
          } @empty {
            <tr>
              <td colspan="3" class="metadata__empty">No field matches “{{ filter() }}”.</td>
            </tr>
          }
        </tbody>
      </table>
    </div>`,
  styleUrls: ['./viewer-shared.scss', './metadata-view.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'viewer-mode' },
})
export class ViewerMetadata {
  readonly metadata = input.required<DocumentResource>();

  protected readonly filter = signal('');
  protected readonly caption = computed(() => `Fields of ${this.metadata().controlNumber}`);
  private readonly rows = computed<Row[]>(() =>
    this.metadata()
      .fields.filter((f) => !f.isHidden)
      .map((f) => {
        const value = f.displayValue ?? '';
        const raw =
          f.rawValue !== null && f.rawValue !== '' && f.rawValue !== value ? f.rawValue : null;
        return { id: String(f.fieldId), label: f.displayName, value, raw, type: f.typeLabel };
      }),
  );
  protected readonly visible = computed(() => {
    const q = this.filter().trim().toLowerCase();
    return q
      ? this.rows().filter(
          (r) => r.label.toLowerCase().includes(q) || r.value.toLowerCase().includes(q),
        )
      : this.rows();
  });
  protected readonly countText = computed(() => {
    const all = this.rows().length;
    const shown = this.visible().length;
    return shown === all ? `${all} fields` : `${shown} of ${all} fields`;
  });
}
