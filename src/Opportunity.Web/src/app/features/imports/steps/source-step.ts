import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { ImportMode } from '../../../core/api/generated/models';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { Select, SelectOption, TextField } from '../../../ui';
import { MODE_HINTS, MODE_LABELS, formatBytes } from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';

const MODES: readonly ImportMode[] = ['append', 'overlay', 'appendOverlay'];

/**
 * Step 1, Source & mode: the load file (DAT, optional OPT) uploaded from the browser, the volume folder inside the
 * server's import share that the load file's relative paths point into, an optional import profile, the name and the
 * import mode. Natives, text and images are never uploaded: they are read from the share.
 */
@Component({
  selector: 'opp-import-source-step',
  imports: [Select, TextField],
  templateUrl: './source-step.html',
  styleUrls: ['./step.scss', './step-radio.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SourceStep {
  protected readonly store = inject(ImportWizardStore);
  private readonly prefs = inject(UiPreferences);

  protected readonly modes = MODES;
  protected readonly modeLabels = MODE_LABELS;
  protected readonly modeHints = MODE_HINTS;

  protected readonly profileOptions = computed<SelectOption[]>(() =>
    this.store.profiles().map((p) => ({ value: p.profileId, label: p.name })),
  );

  protected size(file: File): string {
    return formatBytes(file.size, this.prefs.locale());
  }

  protected async onDat(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    await this.store.setDat(file);
  }

  protected onOpt(event: Event): void {
    this.store.optFile.set((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  protected setVolumeRoot(value: string): void {
    this.store.paths.update((p) => ({ ...p, volumeRoot: value }));
  }

  protected async onProfile(profileId: string): Promise<void> {
    await this.store.applyProfile(profileId);
  }
}
