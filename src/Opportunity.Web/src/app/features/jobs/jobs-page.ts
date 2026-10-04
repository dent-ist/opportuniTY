import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { JobFeed } from '../../core/jobs/job-feed';
import {
  type CreatedBy,
  type JobFilter,
  type JobSummary,
  JOB_TYPES,
  STATUS_OPTIONS,
  canViewAll,
  countText,
  fraction,
  isFinished,
  matchesFilter,
  newer,
  percentText,
  pillStatus,
  searchableText,
  typeLabel,
} from '../../core/jobs/job-model';
import { JobsApi } from '../../core/jobs/jobs-api';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  EmptyState,
  ErrorState,
  LoadingState,
  Progress,
  Select,
  StatusPill,
  TextField,
} from '../../ui';
import { LiveNote } from './live-note';

/**
 * Jobs (familiarity guide §2.3, E06-T07): the workspace's jobs, newest first, with type, status, the two phases Saved
 * and Searchable, errors and creator. Filters by type, status, creator (everyone's jobs only with `Job.ViewAll`) and
 * start date are kept in the URL. Rows follow live updates from the workspace's job feed; each links to the job's
 * page and, where there is one, to the page of what it worked on (an import, an export…).
 */
@Component({
  selector: 'opp-jobs-page',
  imports: [
    Badge,
    Button,
    EmptyState,
    ErrorState,
    LiveNote,
    LoadingState,
    Progress,
    RouterLink,
    Select,
    StatusPill,
    TextField,
  ],
  templateUrl: './jobs-page.html',
  styleUrl: './jobs.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobsPage {
  // Query parameters (bound by the router).
  readonly type = input<string>();
  readonly status = input<string>();
  readonly createdBy = input<string>();
  readonly from = input<string>();
  readonly to = input<string>();

  private readonly api = inject(JobsApi);
  private readonly router = inject(Router);
  private readonly prefs = inject(UiPreferences);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionService);
  private readonly userId = computed(() => this.session.principal()?.userId ?? null);
  protected readonly feed = inject(JobFeed, { optional: true });

  protected readonly viewAll = canViewAll(this.context);
  protected readonly workspaceId = this.context.workspaceId;
  protected readonly typeOptions = JOB_TYPES;
  protected readonly statusOptions = STATUS_OPTIONS;
  protected readonly creatorOptions = [
    { value: 'all', label: 'Everyone' },
    { value: 'me', label: 'Me' },
  ];

  protected readonly filter = computed<JobFilter>(() => ({
    type: this.type() ?? '',
    status: this.status() ?? '',
    createdBy: this.viewAll && this.createdBy() !== 'me' ? 'all' : 'me',
    from: this.from() ?? '',
    to: this.to() ?? '',
  }));
  protected readonly filtered = computed(() => {
    const f = this.filter();
    return !!(f.type || f.status || f.from || f.to || (this.viewAll && f.createdBy === 'me'));
  });

  private readonly loaded = signal<readonly JobSummary[]>([]);
  /** Server time of the newest job read; live jobs created after it are new to the list. */
  private newestCreated = 0;
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  /** Loaded rows at their latest known state, plus matching jobs that started after the list was read. */
  protected readonly rows = computed(() => {
    const live = this.feed?.jobs() ?? new Map<string, JobSummary>();
    const rows = this.loaded().map((job) => newer(job, live.get(job.jobId)));
    const shown = new Set(rows.map((r) => r.jobId));
    const filter = this.filter();
    const started = [...live.values()]
      .filter(
        (j) =>
          !shown.has(j.jobId) &&
          Date.parse(j.createdAt) > this.newestCreated &&
          matchesFilter(j, filter, this.userId()),
      )
      .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
    return [...started, ...rows];
  });

  private readonly numbers = computed(() => new Intl.NumberFormat(this.prefs.locale()));
  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  constructor() {
    effect(() => {
      const filter = this.filter();
      untracked(() => void this.load(filter));
    });
  }

  protected setFilter(change: Partial<Record<keyof JobFilter, string>>): void {
    const current = this.filter();
    const next = { ...current, ...change };
    void this.router.navigate([], {
      queryParams: {
        type: next.type || null,
        status: next.status || null,
        createdBy: this.viewAll && next.createdBy === 'me' ? 'me' : null,
        from: next.from || null,
        to: next.to || null,
      },
      replaceUrl: true,
    });
  }

  protected clearFilters(): void {
    void this.router.navigate([], { queryParams: {}, replaceUrl: true });
  }

  protected creatorValue(): CreatedBy {
    return this.filter().createdBy;
  }

  protected async load(filter = this.filter()): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.list(filter);
      this.newestCreated = Math.max(0, ...page.items.map((j) => Date.parse(j.createdAt) || 0));
      this.loaded.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
      this.loaded.set([]);
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    if (this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.list(this.filter(), this.nextCursor());
      const seen = new Set(this.loaded().map((j) => j.jobId));
      this.loaded.update((items) => [...items, ...page.items.filter((j) => !seen.has(j.jobId))]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected pill(job: JobSummary) {
    return pillStatus(job.status);
  }

  protected typeName(job: JobSummary): string {
    return typeLabel(job.type);
  }

  protected savedValue(job: JobSummary): number {
    return fraction(job.committed, isFinished(job.status)) * 100;
  }

  protected savedText(job: JobSummary): string {
    const locale = this.prefs.locale();
    const value = fraction(job.committed, isFinished(job.status));
    return `${percentText(value, locale)} saved, ${countText(job.committed, locale)}`;
  }

  protected searchableValue(job: JobSummary): number {
    return fraction(job.searchable) * 100;
  }

  protected searchableText(job: JobSummary): string {
    const locale = this.prefs.locale();
    return `${percentText(fraction(job.searchable), locale)} searchable, ${countText(job.searchable, locale)}`;
  }

  protected searchableState(job: JobSummary): string {
    return searchableText(job);
  }

  /** `imports/{id}` → `['/w', ws, 'imports', id]`. */
  protected ownPage(job: JobSummary): string[] | null {
    if (!job.link) return null;
    return ['/w', this.workspaceId, ...job.link.split('/').filter(Boolean)];
  }

  protected num(value: number): string {
    return this.numbers().format(value);
  }

  protected date(value: string | null): string {
    return value ? this.dates().format(new Date(value)) : '';
  }
}
