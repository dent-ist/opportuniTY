import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type {
  DocumentFieldValueResource,
  DocumentResource,
} from '../../../../core/api/generated/models';
import { DisplayFormat, Formatter } from '../../../../core/i18n';
import { Icon, Tooltip } from '../../../../ui';

interface Row {
  readonly id: string;
  readonly label: string;
  readonly value: string;
  /** Tooltip: the value as imported (or, for an instant, as stored in UTC) when it differs from the display value. */
  readonly raw: string | null;
  readonly type: string;
}

/**
 * Metadata mode (E16-T04, familiarity guide §3.2): every field the reviewer may see, from
 * `GET …/documents/{id}` (already loaded with the document, so no extra request): name, display value (dates and
 * numbers in the user's locale, instants in the display time zone with a zone indicator) with the raw imported string
 * in a tooltip (Q-28, E15-T05), and the field type. A filter narrows long field lists.
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
      <span class="metadata__zone">Times in {{ zone() }}</span>
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
                    [oppTooltip]="row.raw"
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
  private readonly fmt = inject(DisplayFormat);
  protected readonly zone = this.fmt.timeZone;

  protected readonly filter = signal('');
  protected readonly caption = computed(() => `Fields of ${this.metadata().controlNumber}`);
  private readonly rows = computed<Row[]>(() =>
    this.metadata()
      .fields.filter((f) => !f.isHidden)
      .map((f) => {
        const value = displayValue(f, this.fmt.formatter());
        return {
          id: String(f.fieldId),
          label: f.displayName,
          value,
          raw: rawText(f, value),
          type: f.typeLabel,
        };
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

/** The value in the user's locale and display time zone; the server's display string for other formats. */
export function displayValue(f: DocumentFieldValueResource, format: Formatter): string {
  const one = (v: unknown): string | null => {
    if (v === null || v === undefined || typeof v === 'object') return null;
    switch (f.format) {
      case 'dateTime':
        return format.dateTime(String(v));
      case 'date':
        return format.calendarDate(String(v));
      case 'number':
      case 'decimal':
        return format.number(v as number | string);
      case 'bytes':
        return format.bytes(v as number | string);
      default:
        return null;
    }
  };
  const values = Array.isArray(f.value) ? f.value : [f.value];
  const shown = values.map(one);
  return shown.length > 0 && shown.every((v) => v !== null)
    ? shown.join('; ')
    : (f.displayValue ?? '');
}

/** "Imported value: …" when import coerced the value; for an instant without one, the stored UTC value. */
function rawText(f: DocumentFieldValueResource, shown: string): string | null {
  if (f.rawValue !== null && f.rawValue !== '' && f.rawValue !== shown) {
    return `Imported value: ${f.rawValue}`;
  }
  if (f.format === 'dateTime' && typeof f.value === 'string')
    return `Stored value (UTC): ${f.value}`;
  return null;
}
