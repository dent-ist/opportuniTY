import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ProductionResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { Badge, Button, EmptyState, ErrorState, Icon, LoadingState } from '../../ui';
import { HttpProductionApi, ProductionApi } from './production-api';
import { batesRange, stageOf } from './production-model';
import { DisplayFormat } from '../../core/i18n';

/**
 * Productions (familiarity guide §2.3, ticket review E12-T08): every production of the workspace, newest first, with
 * name and version, frozen set ID, prefix and Bates range, documents and Bates numbers, stage and finalized date.
 * A row opens the production page; New Production starts a draft.
 */
@Component({
  selector: 'opp-productions-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './productions-page.html',
  styleUrl: '../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [{ provide: ProductionApi, useClass: HttpProductionApi }],
})
export class ProductionsPage {
  private readonly api = inject(ProductionApi);
  private readonly fmt = inject(DisplayFormat);

  protected readonly items = signal<readonly ProductionResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly stage = stageOf;
  protected readonly range = batesRange;
  protected readonly versionOf = (p: ProductionResource) => Number(p.version);

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.list();
      this.items.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    if (this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.list(this.nextCursor());
      this.items.update((items) => [...items, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected date(value: unknown): string {
    return value ? this.fmt.dateTime(String(value)) : '';
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined ? '—' : this.fmt.number(Number(value));
  }
}
