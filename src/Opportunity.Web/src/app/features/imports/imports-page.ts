import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ImportResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Badge, Button, EmptyState, ErrorState, Icon, LoadingState, StatusPill } from '../../ui';
import { HttpImportApi, ImportApi } from './import-api';
import { JOB_STATUS, MODE_LABELS } from './import-model';

/**
 * Imports: the import history of the workspace (familiarity guide §2.3, ticket review E08-T08), newest first, with
 * mode, start, status, the Saved/Searchable state and the report counters, and the New Import wizard.
 */
@Component({
  selector: 'opp-imports-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink, StatusPill],
  templateUrl: './imports-page.html',
  styleUrl: './imports.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: ImportApi, useClass: HttpImportApi }],
})
export class ImportsPage {
  private readonly api = inject(ImportApi);
  private readonly prefs = inject(UiPreferences);

  protected readonly items = signal<readonly ImportResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly modeLabels = MODE_LABELS;
  protected readonly jobStatus = JOB_STATUS;

  protected readonly n = computed(() => new Intl.NumberFormat(this.prefs.locale()));
  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  constructor() {
    void this.load();
  }

  protected date(value: unknown): string {
    return value ? this.dates().format(new Date(String(value))) : '';
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined ? '' : this.n().format(Number(value));
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
}
