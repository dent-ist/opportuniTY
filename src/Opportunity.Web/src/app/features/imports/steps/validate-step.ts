import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Badge, Button, Checkbox, ErrorState, Icon, LoadingState } from '../../../ui';
import { MODE_LABELS, isMapped } from '../import-model';
import { ImportWizardStore } from '../import-wizard-store';
import { DisplayFormat } from '../../../core/i18n';

/**
 * Step 6, Validate & run: the pre-flight reads the whole load file with the settings and writes nothing. Errors block
 * the import; warnings must be acknowledged; every issue is in the CSV download. "Start import" (the wizard's footer)
 * runs the import as a job.
 */
@Component({
  selector: 'opp-import-validate-step',
  imports: [Badge, Button, Checkbox, ErrorState, Icon, LoadingState],
  templateUrl: './validate-step.html',
  styleUrls: ['./step.scss', './step-table.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ValidateStep {
  protected readonly store = inject(ImportWizardStore);
  private readonly fmt = inject(DisplayFormat);
  protected readonly modeLabels = MODE_LABELS;
  protected readonly n = this.fmt.formatter;

  protected readonly result = computed(() =>
    this.store.preflightStale() ? null : this.store.preflight(),
  );
  protected readonly mappedCount = computed(
    () => (this.store.preview()?.columns ?? []).filter(isMapped).length,
  );
  protected readonly issuesUrl = computed(() =>
    this.result() ? this.store.preflightIssuesUrl() : null,
  );

  protected revalidate(): void {
    void this.store.runPreview().then(() => this.store.runPreflight());
  }
}
