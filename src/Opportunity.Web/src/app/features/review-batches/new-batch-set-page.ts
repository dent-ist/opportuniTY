import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { ReviewBatchSortResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { Button, Checkbox, ErrorState, Icon, Select, SelectOption, TextField } from '../../ui';
import {
  FrozenSet,
  FrozenSetApi,
  HttpFrozenSetApi,
  newIdempotencyKey,
} from '../exports/frozen-set/frozen-set-api';
import { FrozenSetSource } from '../exports/frozen-set/frozen-set-source';
import { HttpReviewBatchApi, ReviewBatchApi } from './review-batch-api';
import { SORT_LABEL } from './review-batch-model';

const SORTS: readonly ReviewBatchSortResource[] = ['controlNumber', 'familyDate', 'documentDate'];

/**
 * Review Batches → New Batch Set (#178, familiarity guide §5.3): a saved search frozen now (or a frozen set), the batch
 * name prefix (batches `{prefix}_0001…`), the maximum batch size, families (and optionally email threads) kept
 * together, an optional order and reviewer group. The frozen set's ID and count are shown before anything is created.
 */
@Component({
  selector: 'opp-new-batch-set-page',
  imports: [Button, Checkbox, ErrorState, FrozenSetSource, Icon, RouterLink, Select, TextField],
  templateUrl: './new-batch-set-page.html',
  styleUrls: ['../exports/delivery.scss', './review-batches.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [
    { provide: ReviewBatchApi, useClass: HttpReviewBatchApi },
    { provide: FrozenSetApi, useClass: HttpFrozenSetApi },
  ],
})
export class NewBatchSetPage {
  private readonly api = inject(ReviewBatchApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly initialSavedSearch = this.route.snapshot.queryParamMap.get('savedSearch');
  protected readonly frozen = signal<FrozenSet | null>(null);
  protected readonly name = signal('');
  protected readonly prefix = signal('');
  protected readonly size = signal('100');
  protected readonly families = signal(true);
  protected readonly threads = signal(false);
  protected readonly sort = signal<ReviewBatchSortResource>('controlNumber');
  protected readonly descending = signal('false');
  protected readonly group = signal('');
  protected readonly problems = signal<readonly string[]>([]);
  protected readonly fieldErrors = signal<Readonly<Record<string, string>>>({});
  protected readonly error = signal<ApiError | null>(null);
  protected readonly busy = signal(false);
  private key = newIdempotencyKey();

  protected readonly sortOptions: SelectOption[] = SORTS.map((s) => ({
    value: s,
    label: s === 'controlNumber' ? 'Control Number (as frozen)' : SORT_LABEL[s],
  }));
  protected readonly directionOptions: SelectOption[] = [
    { value: 'false', label: 'Earliest first' },
    { value: 'true', label: 'Latest first' },
  ];

  protected setSort(value: string): void {
    this.sort.set(value as ReviewBatchSortResource);
  }

  protected async create(): Promise<void> {
    if (this.busy()) return;
    const set = this.frozen();
    const size = Number(this.size());
    const problems = [
      ...(set ? [] : ['Freeze the documents to review first.']),
      ...(this.name().trim() ? [] : ['Give the Batch Set a name.']),
      ...(/^[A-Za-z0-9][A-Za-z0-9 ._-]{0,49}$/.test(this.prefix().trim())
        ? []
        : [
            'The batch prefix has 1 to 50 letters, digits, spaces, dots, hyphens or underscores, starting with a letter or digit.',
          ]),
      ...(Number.isInteger(size) && size >= 1 && size <= 10000
        ? []
        : ['The maximum batch size is a whole number from 1 to 10,000.']),
    ];
    this.problems.set(problems);
    this.fieldErrors.set({});
    this.error.set(null);
    if (problems.length || !set) return;
    this.busy.set(true);
    try {
      const created = await this.api.createSet(
        {
          name: this.name().trim(),
          snapshotId: set.snapshotId,
          batchPrefix: this.prefix().trim(),
          maxBatchSize: size,
          keepFamiliesTogether: this.families() || this.threads(),
          keepThreadsTogether: this.threads(),
          sort: this.sort(),
          sortDescending: this.sort() !== 'controlNumber' && this.descending() === 'true',
          reviewerGroup: this.group().trim() || null,
        },
        this.key,
      );
      await this.router.navigate(['..', created.batchSetId], { relativeTo: this.route });
    } catch (e) {
      const error = toApiError(e);
      const errors = error.problem.errors ?? {};
      if (Object.keys(errors).length > 0) {
        this.fieldErrors.set(
          Object.fromEntries(Object.entries(errors).map(([k, v]) => [k, v.join(' ')])),
        );
      } else if (error.status === 409) {
        this.fieldErrors.set({
          batchPrefix: error.problem.detail ?? 'Another Batch Set uses this prefix.',
        });
      } else {
        this.error.set(error);
      }
      // A corrected request is a new action.
      this.key = newIdempotencyKey();
    } finally {
      this.busy.set(false);
    }
  }
}
