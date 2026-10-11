import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ExportResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Button, EmptyState, ErrorState, Icon, LoadingState, StatusPill } from '../../ui';
import { ExportApi, HttpExportApi } from './export-api';
import { EXPORT_STATUS } from './export-model';

/**
 * Exports (familiarity guide §2.3): the export history, newest first: your own exports, or everyone's with
 * Job.ViewAll. Each row shows its frozen set ID and document count (E12-T08), and New Export opens the wizard.
 */
@Component({
  selector: 'opp-exports-page',
  imports: [Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink, StatusPill],
  templateUrl: './exports-page.html',
  styleUrl: './delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [{ provide: ExportApi, useClass: HttpExportApi }],
})
export class ExportsPage {
  private readonly api = inject(ExportApi);
  private readonly prefs = inject(UiPreferences);

  protected readonly items = signal<readonly ExportResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly status = EXPORT_STATUS;
  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

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
    return value ? this.dates().format(new Date(String(value))) : '';
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined
      ? '—'
      : Number(value).toLocaleString(this.prefs.locale());
  }

  /** The frozen set's documents once the export ran (exported plus excluded); the detail page reads the set itself. */
  protected documents(e: ExportResource): number | null {
    return e.report
      ? Number(e.report.documentsExported) + Number(e.report.documentsExcluded)
      : null;
  }
}
