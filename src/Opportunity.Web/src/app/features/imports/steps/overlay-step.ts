import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Checkbox, Select } from '../../../ui';
import { OverlayOptions } from '../import-api';
import { MODE_LABELS } from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';

/**
 * Step 4 (Overlay and Append/Overlay only), Overlay settings: the overlay key, what blank load-file values do
 * ("Leave existing values" by default), how multi-value fields combine, and the per-import switch that lets coding and
 * privilege fields be overlaid (Q-31, recorded in the audit log).
 */
@Component({
  selector: 'opp-import-overlay-step',
  imports: [Checkbox, Select],
  templateUrl: './overlay-step.html',
  styleUrls: ['./step.scss', './step-radio.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OverlayStep {
  protected readonly store = inject(ImportWizardStore);
  protected readonly modeLabels = MODE_LABELS;
  /** Only fields declared unique can match rows; Control Number is the one every workspace has. */
  protected readonly keyFields = [{ value: 'ControlNumber', label: 'Control Number' }];

  protected set<K extends keyof OverlayOptions>(key: K, value: OverlayOptions[K]): void {
    this.store.overlay.update((o) => ({ ...o, [key]: value }));
  }
}
