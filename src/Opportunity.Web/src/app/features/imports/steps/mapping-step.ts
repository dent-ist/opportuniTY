import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { ColumnPreview, MappingTarget } from '../../../core/api/generated/models';
import { toApiError } from '../../../core/api/problem-details';
import { Button, ErrorState, Icon, LoadingState, TextField, ToastService } from '../../../ui';
import {
  IGNORE,
  SEVERAL,
  columnStatus,
  isMapped,
  selectionOf,
  targetKey,
  targetLabels,
  targetTypes,
} from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';

interface TargetOption {
  readonly value: string;
  readonly label: string;
  readonly disabled: boolean;
  readonly target: MappingTarget;
}

/**
 * Step 3, Field mapping: one row per load-file column with sample values, the workspace field it loads into and that
 * field's type, and a status that says why it is mapped ("matched by alias BEGDOC") or that it is not imported.
 * Auto-map, Clear and Save as Import profile. Coding and privilege fields are offered only when the overlay settings
 * allow coding-field overlay (Q-31).
 */
@Component({
  selector: 'opp-import-mapping-step',
  imports: [Button, ErrorState, Icon, LoadingState, TextField],
  templateUrl: './mapping-step.html',
  styleUrls: ['./step.scss', './step-table.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MappingStep {
  protected readonly store = inject(ImportWizardStore);
  private readonly toasts = inject(ToastService);

  protected readonly IGNORE = IGNORE;
  protected readonly SEVERAL = SEVERAL;
  protected readonly status = columnStatus;
  protected readonly labels = targetLabels;
  protected readonly types = targetTypes;
  protected readonly selection = selectionOf;
  protected readonly mapped = isMapped;

  protected readonly saving = signal(false);
  protected readonly profileName = signal('');
  protected readonly saveOpen = signal(false);
  protected readonly saveError = signal<string | null>(null);

  protected readonly columns = computed(() => this.store.preview()?.columns ?? []);
  protected readonly unmapped = computed(() => this.columns().filter((c) => !isMapped(c)));
  protected readonly unmappedNames = computed(() =>
    this.unmapped()
      .map((c) => c.column)
      .join(', '),
  );

  /** Every target, structural targets first (the API's order), plus new fields a profile creates. */
  protected readonly options = computed<TargetOption[]>(() => {
    const coding = this.store.mode() !== 'append' && this.store.overlay().allowCodingFields;
    const options: TargetOption[] = this.store.targets().map((t) => ({
      value: targetKey(t.target),
      label: t.isCodingField ? `${t.label} (coding field)` : t.label,
      disabled: t.isCodingField && !coding,
      target: t.target,
    }));
    const known = new Set(options.map((o) => o.value));
    for (const c of this.columns()) {
      for (const t of c.targets) {
        const value = targetKey(t.target);
        if (!known.has(value)) {
          known.add(value);
          options.push({ value, label: t.label, disabled: false, target: t.target });
        }
      }
    }
    return options;
  });

  protected errors(column: ColumnPreview): number {
    return Number(column.errorCount);
  }

  protected samples(column: ColumnPreview): string {
    return column.sampleValues.filter((v) => v !== '').join(' · ');
  }

  protected onSelect(column: ColumnPreview, value: string): void {
    if (value === SEVERAL) return;
    if (value === IGNORE) this.store.mapColumn(column.column, 'ignore');
    else if (!value) this.store.mapColumn(column.column, null);
    else {
      const option = this.options().find((o) => o.value === value);
      if (option) this.store.mapColumn(column.column, [option.target]);
    }
    void this.store.runPreview();
  }

  protected autoMap(): void {
    this.store.autoMapAll();
    void this.store.runPreview();
  }

  protected clear(): void {
    this.store.clearMapping();
    void this.store.runPreview();
  }

  protected async save(): Promise<void> {
    const name = this.profileName().trim();
    if (!name) {
      this.saveError.set('Enter a name for the import profile.');
      return;
    }
    this.saving.set(true);
    this.saveError.set(null);
    try {
      await this.store.saveProfile(name);
      this.saveOpen.set(false);
      this.toasts.show(`Import profile "${name}" saved.`, { tone: 'success' });
    } catch (e) {
      const error = toApiError(e);
      this.saveError.set(
        error.status === 409
          ? 'An import profile with this name exists. Choose another name.'
          : 'The import profile could not be saved. Try again.',
      );
    } finally {
      this.saving.set(false);
    }
  }
}
