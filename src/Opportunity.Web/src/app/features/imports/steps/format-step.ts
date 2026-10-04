import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { RowPreview } from '../../../core/api/generated/models';
import { Button, Checkbox, ErrorState, Icon, LoadingState, Select, TextField } from '../../../ui';
import { LoadFileOptions } from '../import-api';
import { DELIMITER_PRESETS, ENCODINGS, describeDelimiter } from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';

const ENCODING_SOURCES: Record<string, string> = {
  byteOrderMark: 'detected from the byte-order mark',
  heuristic: 'detected from the content',
  override: 'chosen',
};

/**
 * Step 2, File format: the delimiters, quote and encoding the preview used (detected or from the profile), each
 * overridable, and the first 20 rows parsed with them, so wrong delimiters or mojibake are obvious. Delimiters are
 * shown as glyph and decimal code, the way practitioners specify them.
 */
@Component({
  selector: 'opp-import-format-step',
  imports: [Button, Checkbox, ErrorState, Icon, LoadingState, Select, TextField],
  templateUrl: './format-step.html',
  styleUrls: ['./step.scss', './step-table.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormatStep {
  protected readonly store = inject(ImportWizardStore);
  protected readonly presets = DELIMITER_PRESETS.map((p) => ({ value: p.value, label: p.label }));
  protected readonly encodings = ENCODINGS.map((e) => ({ value: e.value, label: e.label }));
  protected readonly describe = describeDelimiter;

  protected readonly file = computed(() => this.store.preview()?.file ?? null);
  protected readonly custom = computed(() => this.store.loadFile().delimiters === 'custom');
  protected readonly detectedLabel = computed(() => {
    const preset = this.store.detectedPreset();
    return preset ? (DELIMITER_PRESETS.find((p) => p.value === preset)?.label ?? preset) : null;
  });
  protected readonly encodingText = computed(() => {
    const f = this.file();
    if (!f) return '';
    const source = ENCODING_SOURCES[f.encodingSource];
    return source ? `${f.encoding}, ${source}` : f.encoding;
  });
  /** Columns that have parsed values in the preview rows (the API returns cells of mapped columns only). */
  protected readonly header = computed(() => {
    const preview = this.store.preview();
    if (!preview) return [];
    const withCells = new Set(preview.rows.flatMap((r) => r.cells.map((c) => c.column)));
    return preview.file.header.filter((h) => withCells.has(h));
  });
  protected readonly unmappedCount = computed(
    () => (this.file()?.header.length ?? 0) - this.header().length,
  );

  protected cell(row: RowPreview, column: string): string {
    return row.cells.find((c) => c.column === column)?.raw ?? '';
  }

  protected set<K extends keyof LoadFileOptions>(
    key: K,
    value: LoadFileOptions[K],
    refresh = true,
  ): void {
    this.store.loadFile.update((lf) => ({ ...lf, [key]: value }));
    if (refresh) void this.store.runPreview();
  }

  protected refresh(): void {
    void this.store.runPreview();
  }
}
