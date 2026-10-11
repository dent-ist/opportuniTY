import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import type { ProductionResource } from '../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import {
  Announcer,
  Button,
  DialogService,
  ErrorState,
  Icon,
  TextField,
  ToastService,
} from '../../../ui';
import { csvFileName, saveCsv, toCsv, validationRows } from '../../exports/csv';
import { ProductionApi } from '../production-api';
import {
  ENDORSEMENT_SLOTS,
  ProductionForm,
  defaultProductionForm,
  formFromSpecification,
  formProblems,
  nextAvailableStart,
  toSpecification,
} from '../production-model';
import { ProductionStore } from '../production-store';
import { SpecificationForm } from '../specification-form';
import { DisplayFormat } from '../../../core/i18n';

/**
 * Settings tab: a draft's specification, editable (saving with If-Match releases any Bates numbers already assigned,
 * so the range is assigned again); a finalized production's frozen specification read-only, with its SHA-256 and the
 * manifest SHA-256. A draft can be discarded here: its numbers were never produced and become free again.
 */
@Component({
  selector: 'opp-production-settings-tab',
  imports: [Button, ErrorState, Icon, SpecificationForm, TextField],
  templateUrl: './settings-tab.html',
  styleUrl: '../../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsTab {
  protected readonly store = inject(ProductionStore);
  private readonly api = inject(ProductionApi);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly fmt = inject(DisplayFormat);

  protected readonly form = signal<ProductionForm>(defaultProductionForm());
  protected readonly productions = signal<readonly ProductionResource[]>([]);
  protected readonly redactionSets = signal<readonly { id: string; name: string }[]>([]);
  protected readonly saving = signal(false);
  protected readonly discarding = signal(false);
  protected readonly problems = signal<readonly string[]>([]);
  protected readonly errors = signal<readonly string[][]>([]);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly slots = ENDORSEMENT_SLOTS;

  protected readonly spec = computed(() => this.store.production()?.specification ?? null);
  protected readonly specJson = computed(() => JSON.stringify(this.spec(), null, 2));
  protected readonly suggestedStart = computed(() => {
    const p = this.store.production();
    return this.form().prefix.trim()
      ? nextAvailableStart(this.form().prefix, this.productions(), p?.productionId)
      : null;
  });
  protected readonly redactionSetName = computed(() => {
    const id = this.spec()?.redactionSetId;
    return id ? (this.redactionSets().find((s) => s.id === id)?.name ?? id) : null;
  });

  constructor() {
    // The form follows the stored specification whenever the production is read again (save, reload).
    effect(() => {
      const p = this.store.production();
      if (p) untracked(() => this.form.set(formFromSpecification(p.name, p.specification)));
    });
    void this.loadReferenceData();
  }

  private async loadReferenceData(): Promise<void> {
    const [productions, sets] = await Promise.allSettled([
      this.api.list(null, 500),
      this.api.redactionSets(),
    ]);
    if (productions.status === 'fulfilled') this.productions.set(productions.value.items);
    if (sets.status === 'fulfilled') this.redactionSets.set(sets.value);
  }

  protected async save(): Promise<void> {
    const current = this.store.loaded();
    if (!current || this.saving()) return;
    const problems = formProblems(this.form());
    this.problems.set(problems);
    this.errors.set([]);
    this.error.set(null);
    if (problems.length) return;
    if (current.production.bates.state === 'allocated') {
      const confirmed = await this.dialogs.confirm({
        title: 'Save and release Bates numbers',
        message:
          `Saving changes releases the numbers assigned to this draft (${current.production.bates.first} – ` +
          `${current.production.bates.last}). Assign them again on the QC tab before finalizing.`,
        confirmLabel: 'Save changes',
      });
      if (!confirmed) return;
    }
    this.saving.set(true);
    try {
      const saved = await this.api.update(
        current,
        toSpecification(this.form(), current.production.specification),
        this.form().name.trim() || current.production.name,
      );
      this.store.set(saved);
      await this.store.loadQc();
      this.announcer.announce('Specification saved.');
      this.toasts.show('Specification saved.', { tone: 'success' });
    } catch (e) {
      const error = toApiError(e);
      const rows = validationRows(error.problem.errors);
      if (rows.length) this.errors.set(rows);
      else if (error.status === 412) {
        this.error.set(error);
        this.toasts.show('Someone changed this production since you opened it. It was reloaded.', {
          tone: 'warning',
        });
        await this.store.refresh();
      } else this.error.set(error);
    } finally {
      this.saving.set(false);
    }
  }

  protected async discard(): Promise<void> {
    const current = this.store.loaded();
    if (!current || this.discarding()) return;
    const confirmed = await this.dialogs.confirm({
      title: 'Discard this draft',
      message:
        'The draft is kept for the record but can no longer be produced. Bates numbers assigned to it ' +
        'were never produced and become free again.',
      confirmLabel: 'Discard draft',
      tone: 'danger',
    });
    if (!confirmed) return;
    this.discarding.set(true);
    try {
      this.store.set(await this.api.discard(current));
      this.toasts.show('Draft discarded.', { tone: 'success' });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The draft could not be discarded.', {
        tone: 'error',
      });
    } finally {
      this.discarding.set(false);
    }
  }

  protected setName(name: string): void {
    this.form.update((f) => ({ ...f, name }));
  }

  protected downloadErrors(): void {
    saveCsv(
      csvFileName([this.store.production()?.name ?? 'production', 'validation-errors']),
      toCsv([['Setting', 'Problem'], ...this.errors()]),
    );
  }

  protected endorsement(position: string): string {
    return this.spec()?.endorsements?.items?.find((i) => i.position === position)?.template ?? '';
  }

  protected date(value: unknown): string {
    return value ? this.fmt.dateTime(String(value)) : '';
  }
}
