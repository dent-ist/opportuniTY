import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import type { EndorsementPositionResource } from '../../core/api/generated/models';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Checkbox, Select, SelectOption, TextField } from '../../ui';
import { ENDORSEMENT_SLOTS, ProductionForm, batesPreview } from './production-model';

/**
 * The editable part of a production specification (familiarity guide §5.6): Bates numbering with a preview of the
 * first and last number before anything is assigned, privileged handling, images and text, the Redaction Set, the six
 * endorsement slots with a sample-page preview, and the placeholder texts. Used by New Production and the Settings tab
 * of a draft; a finalized production shows its frozen specification read-only instead.
 */
@Component({
  selector: 'opp-specification-form',
  imports: [Checkbox, Select, TextField],
  templateUrl: './specification-form.html',
  styleUrls: ['../exports/delivery.scss', './specification-form.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SpecificationForm {
  private readonly prefs = inject(UiPreferences);

  readonly form = model.required<ProductionForm>();
  /** Documents of the frozen set, for the Bates preview. */
  readonly documents = input<number | null>(null);
  readonly redactionSets = input<readonly { readonly id: string; readonly name: string }[]>([]);
  /** The next free number for the prefix, offered as the start. */
  readonly suggestedStart = input<number | null>(null);

  protected readonly slots = ENDORSEMENT_SLOTS;
  protected readonly imageFormats: SelectOption[] = [
    { value: 'tiffG4', label: 'TIFF Group 4 (black and white)' },
    { value: 'jpeg', label: 'JPEG (colour)' },
  ];
  protected readonly redactionOptions = computed<SelectOption[]>(() =>
    this.redactionSets().map((s) => ({ value: s.id, label: s.name })),
  );
  protected readonly preview = computed(() => batesPreview(this.form(), this.documents()));
  protected readonly sample = computed(() => {
    const p = this.preview();
    const f = this.form();
    const fill = (template: string) =>
      template
        .replaceAll('{bates}', p?.first ?? 'ABC0000001')
        .replaceAll('{confidentiality}', 'CONFIDENTIAL')
        .replaceAll('{production}', f.name || 'Production');
    return Object.fromEntries(
      ENDORSEMENT_SLOTS.map((s) => [s.position, fill(f.endorsements[s.position])]),
    ) as Record<EndorsementPositionResource, string>;
  });

  protected patch(change: Partial<ProductionForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected setSlot(position: EndorsementPositionResource, template: string): void {
    this.form.update((f) => ({ ...f, endorsements: { ...f.endorsements, [position]: template } }));
  }

  protected useSuggested(): void {
    const n = this.suggestedStart();
    if (n !== null) this.patch({ startNumber: String(n) });
  }

  protected num(n: number | null): string {
    return n === null ? '—' : n.toLocaleString(this.prefs.locale());
  }
}
