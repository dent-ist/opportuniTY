import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { JobFeed } from '../../../core/jobs/job-feed';
import { type JobDetail, etaText, fraction, isFinished } from '../../../core/jobs/job-model';
import { JobsApi } from '../../../core/jobs/jobs-api';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { SessionService } from '../../../core/session/session';
import { generationText } from '../../../core/search/search-freshness';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import {
  Announcer,
  Button,
  DialogService,
  ErrorState,
  Icon,
  IconName,
  LoadingState,
  Progress,
  StatusPill,
  ToastService,
} from '../../../ui';
import { formatDate } from '../saved-search-model';
import {
  HttpSearchTermReportApi,
  ReportSnapshot,
  ReportTerm,
  SearchTermReport,
  SearchTermReportApi,
} from './search-term-report-api';
import {
  INDEX_NOT_CURRENT,
  TERM_PARAM,
  TERM_REPORT_PARAM,
  TermSort,
  TermSortKey,
  canDelete,
  canSeeGenerations,
  errorParts,
  isReportFinished,
  nextSort,
  reportErrorText,
  scopeLabel,
  sortTerms,
  statusPill,
  termErrorText,
  visibilityNotice,
} from './search-term-report-model';

/** How often a queued or running report is re-read. */
export const REPORT_POLL_MS = new InjectionToken<number>('REPORT_POLL_MS', { factory: () => 1500 });

interface Column {
  readonly key: Exclude<TermSortKey, 'entered' | 'name'>;
  readonly label: string;
  readonly description: string;
}

/** Count columns in the familiar order: hits, with family, unique, unique with family. */
const COLUMNS: readonly Column[] = [
  {
    key: 'documentsWithHits',
    label: 'Documents with hits',
    description: 'Documents in scope that match the term.',
  },
  {
    key: 'documentsWithHitsIncludingFamily',
    label: 'With family',
    description: 'Documents with hits plus the other members of their families.',
  },
  {
    key: 'uniqueHits',
    label: 'Unique hits',
    description: 'Documents that match this term and no other term of the report.',
  },
  {
    key: 'uniqueHitsIncludingFamily',
    label: 'Unique with family',
    description: 'Unique hits plus the other members of their families.',
  },
];

/**
 * One Search Terms Report (#180, familiarity guide §5 step 5): while it runs, its job's progress (with a link to the
 * job); then the frozen set it counted and when it was taken, whose view of the documents the counts are (the person
 * who ran it), a plain warning when the search index was not current, the per-term table (sortable; a term with a
 * syntax error shows the error and where it is, the other terms stay), the totals, CSV/XLSX downloads through the
 * protected-content gateway, "Re-run on the same set" and delete. A term's count opens its documents in Documents.
 */
@Component({
  selector: 'opp-terms-report-page',
  imports: [Button, ErrorState, Icon, LoadingState, Progress, RouterLink, StatusPill],
  templateUrl: './terms-report-page.html',
  styleUrls: ['./terms-reports.scss', './terms-report-results.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: SearchTermReportApi, useClass: HttpSearchTermReportApi }],
})
export class TermsReportPage {
  /** Route parameter. */
  readonly reportId = input.required<string>();

  protected readonly api = inject(SearchTermReportApi);
  private readonly jobs = inject(JobsApi, { optional: true });
  private readonly feed = inject(JobFeed, { optional: true });
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly router = inject(Router);
  private readonly me = inject(SessionService).principal;
  private readonly pollMs = inject(REPORT_POLL_MS);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;
  private rerunKey: string | null = null;

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly columns = COLUMNS;
  protected readonly termReportParam = TERM_REPORT_PARAM;
  protected readonly termParam = TERM_PARAM;
  protected readonly indexNotCurrent = INDEX_NOT_CURRENT;
  protected readonly item = signal<SearchTermReport | null>(null);
  protected readonly job = signal<JobDetail | null>(null);
  protected readonly snapshot = signal<ReportSnapshot | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly sort = signal<TermSort>({ key: 'entered', direction: 'asc' });
  protected readonly rerunning = signal(false);

  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';
  private readonly caller = computed(() => ({
    userId: this.me()?.userId ?? null,
    can: (p: string) => this.context.can(p),
  }));
  protected readonly n = computed(() => new Intl.NumberFormat(this.prefs.locale()));

  protected readonly finished = computed(() => {
    const i = this.item();
    return !!i && isReportFinished(i.status);
  });
  protected readonly completed = computed(() => this.item()?.status === 'completed');
  protected readonly pill = computed(() => statusPill(this.item()?.status ?? 'queued'));
  protected readonly scope = computed(() => {
    const i = this.item();
    return i ? scopeLabel(i.scope) : '';
  });
  protected readonly runBy = computed(() => {
    const i = this.item();
    if (!i) return '';
    return i.createdBy.userId === this.caller().userId ? 'You' : i.createdBy.displayName;
  });
  protected readonly visibility = computed(() => {
    const i = this.item();
    return i ? visibilityNotice(i.createdBy, this.caller().userId) : '';
  });
  /** Rerun and delete: its creator or a workspace admin (#72). */
  protected readonly mayDelete = computed(() => {
    const i = this.item();
    return !!i && canDelete(i, this.caller());
  });
  protected readonly generation = computed(() => {
    const i = this.item();
    if (!i?.searchGeneration || !canSeeGenerations(this.caller())) return null;
    return generationText(i.searchGeneration, this.prefs.locale());
  });
  protected readonly frozenAt = computed(() => {
    const at = this.snapshot()?.frozenAt;
    return at ? formatDate(at, this.prefs.locale(), this.timeZone) : null;
  });
  protected readonly frozenCount = computed(() => {
    const count = this.snapshot()?.documentCount;
    if (count === null || count === undefined) return null;
    return `${this.n().format(count)} ${count === 1 ? 'document' : 'documents'}`;
  });

  protected date(value: string | null | undefined): string {
    return value ? formatDate(value, this.prefs.locale(), this.timeZone) : '—';
  }

  protected num(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : this.n().format(value);
  }

  /** Progress of the counting job: a fraction, or undefined while not known (indeterminate). */
  protected readonly progress = computed(() => {
    const job = this.job();
    if (!job || !job.committed.total) return undefined;
    return fraction(job.committed, isFinished(job.status));
  });
  protected readonly progressText = computed(() => {
    const job = this.job();
    const status = this.item()?.status;
    const parts = [status === 'queued' ? 'Waiting to start' : 'Freezing the scope and counting'];
    const eta = etaText(job?.etaSeconds);
    if (eta) parts.push(eta);
    return parts.join(' · ');
  });

  protected readonly terms = computed(() => sortTerms(this.item()?.terms ?? [], this.sort()));
  protected readonly errorCount = computed(
    () => (this.item()?.terms ?? []).filter((t) => t.error).length,
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer) clearTimeout(this.timer);
    });
    // A new report id (the list, a re-run that made a new report) loads from scratch.
    effect(() => {
      this.reportId();
      untracked(() => {
        if (this.timer) clearTimeout(this.timer);
        this.item.set(null);
        this.job.set(null);
        this.snapshot.set(null);
        this.error.set(null);
        void this.load();
      });
    });
  }

  protected async load(): Promise<void> {
    if (this.destroyed) return;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const id = this.reportId();
    const wasFinished = this.finished();
    try {
      const item = await this.api.get(id);
      if (id !== this.reportId()) return;
      this.item.set(item);
      this.error.set(null);
      if (!isReportFinished(item.status)) {
        await this.readJob(item);
        this.schedule();
      } else {
        if (item.jobId && this.job()) await this.readJob(item);
        if (!wasFinished && this.job() !== null)
          this.announcer.announce(
            item.status === 'completed'
              ? `Report ${item.name} completed.`
              : `Report ${item.name} failed.`,
          );
      }
      if (item.snapshotId && this.snapshot()?.snapshotId !== item.snapshotId)
        void this.readSnapshot(item.snapshotId);
    } catch (e) {
      if (id !== this.reportId()) return;
      this.error.set(toApiError(e));
      // Keep following a running report through a passing network failure.
      const i = this.item();
      if (i && !isReportFinished(i.status)) this.schedule();
    }
  }

  private async readJob(item: SearchTermReport): Promise<void> {
    if (!item.jobId || !this.jobs) return;
    try {
      const job = await this.jobs.get(item.jobId);
      this.job.set(job);
      this.feed?.observe(job);
    } catch {
      // The job may not be visible (another user's report): progress shows as indeterminate.
    }
  }

  private async readSnapshot(snapshotId: string): Promise<void> {
    try {
      this.snapshot.set(await this.api.snapshot(snapshotId));
    } catch {
      // The id is still shown; only when it was taken is missing.
    }
  }

  private schedule(): void {
    if (this.destroyed) return;
    this.timer = setTimeout(() => void this.load(), this.pollMs);
  }

  protected exportUrl(format: 'csv' | 'xlsx'): string {
    return this.api.exportUrl(this.reportId(), format);
  }

  protected termParams(t: ReportTerm): Record<string, string> {
    return { [TERM_REPORT_PARAM]: this.reportId(), [TERM_PARAM]: t.termId };
  }

  protected errorText = termErrorText;
  protected errorParts(t: ReportTerm) {
    return t.error ? errorParts(t.expression, t.error.position) : null;
  }

  // ── Sorting ─────────────────────────────────────────────────────────────────────────────────────────────────

  protected sortBy(key: TermSortKey): void {
    const next = nextSort(this.sort(), key);
    this.sort.set(next);
    const label = key === 'name' ? 'Term' : (COLUMNS.find((c) => c.key === key)?.label ?? key);
    this.announcer.announce(
      next.key === 'entered'
        ? 'Terms in the order entered.'
        : `Sorted by ${label}, ${next.direction === 'asc' ? 'ascending' : 'descending'}.`,
    );
  }

  protected ariaSort(key: TermSortKey): 'ascending' | 'descending' | null {
    const s = this.sort();
    if (s.key !== key) return null;
    return s.direction === 'asc' ? 'ascending' : 'descending';
  }

  protected sortIcon(key: TermSortKey): IconName {
    const s = this.sort();
    return s.key === key && s.direction === 'asc' ? 'chevron-up' : 'chevron-down';
  }

  // ── Actions ─────────────────────────────────────────────────────────────────────────────────────────────────

  /** "Re-run on the same set": the same frozen set and terms, so the same numbers (Q-30). */
  protected async rerun(): Promise<void> {
    const item = this.item();
    if (!item || this.rerunning()) return;
    this.rerunKey ??=
      globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    this.rerunning.set(true);
    try {
      const result = await this.api.rerun(item.reportId, this.rerunKey);
      this.rerunKey = null;
      this.toasts.show('Re-running the report on the same frozen set.', { tone: 'info' });
      if (result && result.reportId && result.reportId !== item.reportId) {
        await this.router.navigate([
          '/w',
          this.workspaceId,
          'searches',
          'terms-reports',
          result.reportId,
        ]);
        return;
      }
      if (result) this.item.set(result);
      this.job.set(null);
      await this.load();
    } catch (e) {
      this.toasts.show(reportErrorText(toApiError(e)), { tone: 'error' });
    } finally {
      this.rerunning.set(false);
    }
  }

  protected async remove(): Promise<void> {
    const item = this.item();
    if (!item) return;
    const ok = await this.dialogs.confirm({
      title: 'Delete Search Terms Report?',
      message: `“${item.name}” and its results will be deleted. Documents, their coding and the frozen set it counted are not changed.`,
      confirmLabel: 'Delete report',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await this.api.delete(item.reportId);
      this.toasts.show(`Search Terms Report “${item.name}” deleted.`, { tone: 'success' });
      await this.router.navigate(['/w', this.workspaceId, 'searches', 'terms-reports']);
    } catch (e) {
      this.toasts.show(reportErrorText(toApiError(e)), { tone: 'error' });
    }
  }
}
