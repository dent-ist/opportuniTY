import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ImportResource, ImportRowIssueResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Badge, Button, ErrorState, Icon, LoadingState, Progress, StatusPill } from '../../ui';
import { HttpImportApi, ImportApi } from './import-api';
import {
  JOB_STATUS,
  MODE_LABELS,
  isJobFinished,
  isJobSettled,
  savedProgress,
  searchableProgress,
} from './import-model';

/** How often a running import is re-read. */
export const IMPORT_POLL_MS = new InjectionToken<number>('IMPORT_POLL_MS', { factory: () => 1500 });

/** Row errors shown on the page; all of them are in the error file and the report. */
const ERROR_ROWS = 50;

/**
 * One import (guide §5.1 steps 7–8): the job's progress in two phases, Saved (stored in the database) and Searchable
 * (the job's index state), then the report: rows read / imported / overlaid / unchanged / errored, the import report
 * CSV, the error file in the source's delimiters and encoding (re-importable once fixed), the first row errors, and
 * links to the Jobs section and to re-importing the corrected error file with the same profile and mode.
 */
@Component({
  selector: 'opp-import-detail',
  imports: [Badge, Button, ErrorState, Icon, LoadingState, Progress, RouterLink, StatusPill],
  templateUrl: './import-detail.html',
  styleUrl: './imports.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: ImportApi, useClass: HttpImportApi }],
})
export class ImportDetail {
  /** Route parameter. */
  readonly importId = input.required<string>();

  protected readonly api = inject(ImportApi);
  private readonly prefs = inject(UiPreferences);
  private readonly pollMs = inject(IMPORT_POLL_MS);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  protected readonly item = signal<ImportResource | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly rowErrors = signal<readonly ImportRowIssueResource[]>([]);
  protected readonly modeLabels = MODE_LABELS;

  protected readonly n = computed(() => new Intl.NumberFormat(this.prefs.locale()));
  protected readonly status = computed(() => {
    const i = this.item();
    return i ? JOB_STATUS[i.job.status] : 'queued';
  });
  protected readonly finished = computed(() => {
    const i = this.item();
    return !!i && isJobFinished(i.job);
  });
  protected readonly saved = computed(() => {
    const i = this.item();
    return i ? savedProgress(i.job) : null;
  });
  protected readonly searchable = computed(() => {
    const i = this.item();
    return i ? searchableProgress(i.job) : null;
  });
  protected readonly errored = computed(() => Number(this.item()?.report.rowsErrored ?? 0));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer) clearTimeout(this.timer);
    });
    queueMicrotask(() => void this.load());
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined ? '–' : this.n().format(Number(value));
  }

  protected async load(): Promise<void> {
    if (this.destroyed) return;
    try {
      const item = await this.api.get(this.importId());
      const wasFinished = this.finished();
      this.item.set(item);
      this.error.set(null);
      if (isJobFinished(item.job) && !wasFinished && Number(item.report.rowsErrored) > 0) {
        this.rowErrors.set(await this.api.errors(item.importId, ERROR_ROWS));
      }
      if (!isJobSettled(item.job)) this.schedule();
    } catch (e) {
      this.error.set(toApiError(e));
      // Keep following a running import through a passing network failure.
      if (this.item() && !isJobSettled(this.item()!.job)) this.schedule();
    }
  }

  private schedule(): void {
    if (this.destroyed) return;
    this.timer = setTimeout(() => void this.load(), this.pollMs);
  }
}
