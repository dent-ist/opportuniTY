import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { ProductionResource, ProductionSpecification } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { Button, ErrorState, Icon, TextField } from '../../ui';
import { csvFileName, saveCsv, toCsv, validationRows } from '../exports/csv';
import { FrozenSet, FrozenSetApi, HttpFrozenSetApi } from '../exports/frozen-set/frozen-set-api';
import { FrozenSetSource } from '../exports/frozen-set/frozen-set-source';
import { HttpProductionApi, ProductionApi } from './production-api';
import {
  ProductionForm,
  defaultProductionForm,
  formFromSpecification,
  formProblems,
  nextAvailableStart,
  toSpecification,
} from './production-model';
import { SpecificationForm } from './specification-form';

/**
 * Productions → New Production (familiarity guide §5.6): name, source (a saved search frozen now with Include: Family,
 * or a frozen set; its ID and count are shown), and the specification with a Bates preview. The start number defaults
 * to the next available number for the prefix. "Copy settings" (`?copyFrom=<productionId>`) starts from a prior
 * production's specification. Creating the draft goes to the production page, where the work continues across days.
 */
@Component({
  selector: 'opp-new-production-page',
  imports: [Button, ErrorState, FrozenSetSource, Icon, RouterLink, SpecificationForm, TextField],
  templateUrl: './new-production-page.html',
  styleUrl: '../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [
    { provide: ProductionApi, useClass: HttpProductionApi },
    { provide: FrozenSetApi, useClass: HttpFrozenSetApi },
  ],
})
export class NewProductionPage {
  private readonly api = inject(ProductionApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly initialSavedSearch = this.route.snapshot.queryParamMap.get('savedSearch');
  private readonly copyFrom = this.route.snapshot.queryParamMap.get('copyFrom');

  protected readonly frozen = signal<FrozenSet | null>(null);
  protected readonly form = signal<ProductionForm>(defaultProductionForm());
  protected readonly productions = signal<readonly ProductionResource[]>([]);
  protected readonly redactionSets = signal<readonly { id: string; name: string }[]>([]);
  protected readonly copiedFrom = signal<ProductionResource | null>(null);
  private copiedSpecification: ProductionSpecification | null = null;
  protected readonly problems = signal<readonly string[]>([]);
  protected readonly errors = signal<readonly string[][]>([]);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly busy = signal(false);

  protected readonly suggestedStart = computed(() =>
    this.form().prefix.trim() ? nextAvailableStart(this.form().prefix, this.productions()) : null,
  );

  constructor() {
    void this.start();
  }

  private async start(): Promise<void> {
    const [productions, sets] = await Promise.allSettled([
      this.api.list(null, 500),
      this.api.redactionSets(),
    ]);
    if (productions.status === 'fulfilled') this.productions.set(productions.value.items);
    if (sets.status === 'fulfilled') this.redactionSets.set(sets.value);
    if (!this.copyFrom) return;
    try {
      const { production } = await this.api.get(this.copyFrom);
      this.copiedFrom.set(production);
      this.copiedSpecification = production.specification;
      const form = formFromSpecification(`${production.name} (copy)`, production.specification);
      // A copy continues the numbering: the next free number, never the source's range.
      form.startNumber = String(nextAvailableStart(form.prefix, this.productions()));
      this.form.set(form);
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  protected setName(name: string): void {
    this.form.update((f) => ({ ...f, name }));
  }

  protected async create(): Promise<void> {
    if (this.busy()) return;
    const set = this.frozen();
    const problems = [
      ...(set ? [] : ['Freeze the documents to produce first.']),
      ...formProblems(this.form()),
    ];
    this.problems.set(problems);
    this.errors.set([]);
    this.error.set(null);
    if (problems.length || !set) return;
    this.busy.set(true);
    try {
      const created = await this.api.create({
        snapshotId: set.snapshotId,
        ...(this.form().name.trim() ? { name: this.form().name.trim() } : {}),
        specification: toSpecification(this.form(), this.copiedSpecification),
      });
      await this.router.navigate(['..', created.production.productionId], {
        relativeTo: this.route,
      });
    } catch (e) {
      const error = toApiError(e);
      const rows = validationRows(error.problem.errors);
      if (rows.length) this.errors.set(rows);
      else this.error.set(error);
    } finally {
      this.busy.set(false);
    }
  }

  protected downloadErrors(): void {
    saveCsv(
      csvFileName([this.form().name || 'New-production', 'validation-errors']),
      toCsv([['Setting', 'Problem'], ...this.errors()]),
    );
  }
}
