import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Checkbox, Icon, Select, SelectOption, TextField } from '../../../ui';
import { PathOptions } from '../import-api';
import { columnFor } from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';

/**
 * Step 5, Natives, text & images: which columns hold the native and extracted-text paths (the Native Path and
 * Extracted Text Path targets of the mapping), path rebasing with a live example, missing-file behaviour, and how the
 * OPT's image keys are matched. Files are read from the volume folder chosen in step 1.
 */
@Component({
  selector: 'opp-import-files-step',
  imports: [Checkbox, Icon, Select, TextField],
  templateUrl: './files-step.html',
  styleUrl: './step.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FilesStep {
  protected readonly store = inject(ImportWizardStore);

  protected readonly columnOptions = computed<SelectOption[]>(() =>
    (this.store.preview()?.columns ?? []).map((c) => ({ value: c.column, label: c.column })),
  );
  protected readonly nativeColumn = computed(() =>
    columnFor(this.store.preview()?.columns ?? [], 'nativePath'),
  );
  protected readonly textColumn = computed(() =>
    columnFor(this.store.preview()?.columns ?? [], 'textPath'),
  );
  protected readonly missingOptions: SelectOption[] = [
    { value: 'flag', label: 'Flag the document and continue' },
    { value: 'error', label: 'Do not load the row' },
  ];
  protected readonly matchOptions: SelectOption[] = [
    { value: 'controlNumber', label: 'Control Number' },
    { value: 'begBates', label: 'Begin Bates' },
  ];
  protected readonly volume = computed(
    () => this.store.paths().volumeRoot?.trim() || 'the import share',
  );

  /** The first native path of the preview, before and after rebasing (guide §5.1 step 5). */
  protected readonly example = computed(() => {
    const column = this.nativeColumn() || this.textColumn();
    const sample = this.store
      .preview()
      ?.columns.find((c) => c.column === column)
      ?.sampleValues.find((v) => v);
    if (!sample) return null;
    const prefix = this.store.paths().stripPrefix?.trim() ?? '';
    let rest = sample;
    if (prefix && sample.toLowerCase().startsWith(prefix.toLowerCase())) {
      rest = sample.slice(prefix.length);
    }
    rest = rest.replace(/\\/g, '/').replace(/^\/+/, '');
    const root = this.store.paths().volumeRoot?.trim().replace(/\\/g, '/').replace(/\/+$/, '');
    return { from: sample, to: root ? `${root}/${rest}` : rest };
  });

  protected setColumn(target: 'nativePath' | 'textPath', column: string): void {
    this.store.setStructuralColumn(target, column);
    void this.store.runPreview();
  }

  protected setPath<K extends keyof PathOptions>(key: K, value: PathOptions[K]): void {
    this.store.paths.update((p) => ({ ...p, [key]: value }));
  }

  protected setMatch(value: string): void {
    this.store.imageMatchBy.set(value === 'begBates' ? 'begBates' : 'controlNumber');
  }
}
