import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import type {
  AuditActionResource,
  AuditEventResource,
  AuditExportResource,
} from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  Checkbox,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  Select,
  type SelectOption,
  StatusPill,
  TextField,
  ToastService,
} from '../../ui';
import { AuditLogApi, HttpAuditLogApi } from './audit-log-api';
import {
  type AuditFilter,
  EMPTY_FILTER,
  FILTER_PARAMS,
  OUTCOME_OPTIONS,
  actionLabel,
  categoryLabel,
  detailRows,
  exportStatus,
  isActiveExport,
  isFiltered,
  outcomeLabel,
  resourceText,
  shortId,
} from './audit-log-model';

/** How often the page checks on its running exports. */
export const AUDIT_EXPORT_POLL_MS = 3000;

/** Permission names this page checks (the closed catalogue, docs/security/permission-matrix.md). */
const READ_SEARCH_TEXT = 'Audit.ReadSearchText';

/**
 * Admin › Audit (E14-T04, ADR-013 §8): the workspace's audit trail, newest first. Filters (date range, category and
 * action, user, document by Control Number, job, outcome) combine and live in the URL; results arrive one page at a
 * time ("Load more events"), never all at once. A row opens its details (envelope, IDs, the event's details and,
 * for holders of `Audit.ReadSearchText` who ask for it, the executed search text) and offers to narrow the list to
 * its user, document or job. "Export to CSV" runs the current filters as a background job; the export is listed below
 * with its status and, once completed, its download. Every page read and every download is audited by the API, and
 * events about documents the reader may not see never appear.
 */
@Component({
  selector: 'opp-audit-log-page',
  imports: [
    Badge,
    Button,
    Checkbox,
    EmptyState,
    ErrorState,
    Icon,
    LoadingState,
    RouterLink,
    Select,
    StatusPill,
    TextField,
  ],
  templateUrl: './audit-log-page.html',
  styleUrl: './audit-log.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: AuditLogApi, useClass: HttpAuditLogApi }],
})
export class AuditLogPage {
  // Query parameters (bound by the router; names in FILTER_PARAMS).
  readonly from = input<string>();
  readonly to = input<string>();
  readonly category = input<string>();
  readonly action = input<string>();
  readonly user = input<string>();
  readonly userName = input<string>();
  readonly document = input<string>();
  readonly job = input<string>();
  readonly outcome = input<string>();
  readonly searchText = input<string>();

  private readonly api = inject(AuditLogApi);
  private readonly router = inject(Router);
  private readonly prefs = inject(UiPreferences);
  private readonly toasts = inject(ToastService);
  private readonly context = inject(WorkspaceContext);

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly canReadSearchText = this.context.can(READ_SEARCH_TEXT);
  protected readonly outcomeOptions = OUTCOME_OPTIONS;

  protected readonly filter = computed<AuditFilter>(() => ({
    from: this.from() ?? '',
    to: this.to() ?? '',
    category: this.category() ?? '',
    action: this.action() ?? '',
    actor: this.user() ?? '',
    actorName: this.userName() ?? '',
    controlNumber: this.document() ?? '',
    job: this.job() ?? '',
    outcome: this.outcome() ?? '',
    searchText: this.canReadSearchText && this.searchText() === '1' ? '1' : '',
  }));
  protected readonly filtered = computed(() => isFiltered(this.filter()));

  /** Text filters are applied with Apply (or Enter), not on every keystroke. */
  protected readonly documentDraft = linkedSignal(() => this.filter().controlNumber);
  protected readonly jobDraft = linkedSignal(() => this.filter().job);

  private readonly actions = signal<readonly AuditActionResource[]>([]);
  protected readonly categoryOptions = computed<SelectOption[]>(() =>
    [...new Set(this.actions().map((a) => a.category))]
      .map((c) => ({ value: c, label: categoryLabel(c) }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  );
  protected readonly actionOptions = computed<SelectOption[]>(() => {
    const category = this.filter().category;
    return this.actions()
      .filter((a) => a.category === category)
      .map((a) => ({ value: a.action, label: actionLabel(a.action) }))
      .sort((a, b) => a.label.localeCompare(b.label));
  });

  protected readonly events = signal<readonly AuditEventResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly invalid = signal<Readonly<Record<string, string[]>>>({});
  protected readonly expanded = signal<string | null>(null);

  /** Why the list is empty: an unknown document or job, the filters, or a new workspace. */
  protected readonly empty = computed(() => {
    const problem = this.fieldError('controlNumber') ?? this.fieldError('jobId');
    if (problem) return { title: 'No events match these filters', message: problem };
    return this.filtered()
      ? {
          title: 'No events match these filters',
          message: 'Change or clear the filters to see more events.',
        }
      : {
          title: 'No audit events yet',
          message: 'Actions in this workspace are recorded here as they happen.',
        };
  });

  protected readonly exports = signal<readonly AuditExportResource[]>([]);
  protected readonly exporting = signal(false);
  private exportKey: { filter: string; key: string } | null = null;
  private poll: ReturnType<typeof setTimeout> | null = null;

  private readonly dates = computed(
    () =>
      new Intl.DateTimeFormat(this.prefs.locale(), {
        dateStyle: 'medium',
        timeStyle: 'medium',
      }),
  );
  private readonly numbers = computed(() => new Intl.NumberFormat(this.prefs.locale()));

  constructor() {
    effect(() => {
      const filter = this.filter();
      untracked(() => void this.load(filter));
    });
    void this.loadActions();
    void this.loadExports();
    inject(DestroyRef).onDestroy(() => this.stopPolling());
  }

  protected setFilter(change: Partial<AuditFilter>): void {
    const current = this.filter();
    // An action belongs to its category: changing the category clears it.
    const categoryChanged = change.category !== undefined && change.category !== current.category;
    const next: AuditFilter = {
      ...current,
      ...(categoryChanged ? { action: '' } : {}),
      ...change,
    };
    const queryParams: Record<string, string | null> = {};
    for (const key of Object.keys(FILTER_PARAMS) as (keyof AuditFilter)[]) {
      queryParams[FILTER_PARAMS[key]] = next[key] || null;
    }
    if (!next.actor) queryParams[FILTER_PARAMS.actorName] = null;
    void this.router.navigate([], { queryParams, replaceUrl: true });
  }

  protected applyText(event?: Event): void {
    event?.preventDefault();
    this.setFilter({ controlNumber: this.documentDraft().trim(), job: this.jobDraft().trim() });
  }

  protected clearFilters(): void {
    const keep = this.filter().searchText;
    this.setFilter({ ...EMPTY_FILTER, searchText: keep });
  }

  protected async load(filter = this.filter()): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    this.invalid.set({});
    this.expanded.set(null);
    try {
      const page = await this.api.events(filter);
      this.events.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      const error = toApiError(e);
      this.events.set([]);
      this.nextCursor.set(null);
      if (error.code === 'validation') this.invalid.set(error.problem.errors ?? {});
      else this.error.set(error);
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (this.loadingMore() || !cursor) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.events(this.filter(), cursor);
      const seen = new Set(this.events().map((e) => e.eventId));
      this.events.update((items) => [...items, ...page.items.filter((e) => !seen.has(e.eventId))]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected toggle(event: AuditEventResource): void {
    this.expanded.update((id) => (id === event.eventId ? null : event.eventId));
  }

  protected async exportCsv(): Promise<void> {
    if (this.exporting()) return;
    const filter = this.filter();
    const fingerprint = JSON.stringify(filter);
    // A retry of the same request (e.g. after a network error) keeps its key, so it never starts a second export.
    if (this.exportKey?.filter !== fingerprint) {
      this.exportKey = {
        filter: fingerprint,
        key:
          globalThis.crypto?.randomUUID?.() ??
          `${Date.now()}-${Math.random().toString(36).slice(2)}`,
      };
    }
    this.exporting.set(true);
    try {
      const created = await this.api.createExport(filter, this.exportKey.key);
      this.exportKey = null;
      this.exports.update((list) => [
        created,
        ...list.filter((e) => e.exportId !== created.exportId),
      ]);
      this.toasts.show('Audit export started. It is listed under Your exports.', {
        tone: 'success',
      });
      this.schedulePoll();
    } catch (e) {
      const error = toApiError(e);
      this.toasts.show(
        error.code === 'validation'
          ? 'The filters are not valid; correct them and export again.'
          : (error.problem.detail ?? 'The export could not be started.'),
        { tone: 'error' },
      );
    } finally {
      this.exporting.set(false);
    }
  }

  protected downloadUrl(e: AuditExportResource): string {
    return this.api.downloadUrl(e.exportId);
  }

  protected exportPill(e: AuditExportResource) {
    return exportStatus(e);
  }

  protected exportFilters(e: AuditExportResource): string {
    const f = e.filters;
    const parts = [
      f.from || f.to
        ? `${f.from ? this.date(String(f.from)) : '…'} – ${f.to ? this.date(String(f.to)) : '…'}`
        : '',
      f.category ? categoryLabel(f.category) + (f.action ? ` › ${actionLabel(f.action)}` : '') : '',
      f.actorId ? 'one user' : '',
      f.documentId ? 'one document' : '',
      f.jobId ? `job ${shortId(f.jobId)}` : '',
      f.outcome ? outcomeLabel(f.outcome) : '',
      e.includeSearchText ? 'with search text' : '',
    ].filter(Boolean);
    return parts.length ? parts.join(', ') : 'All events';
  }

  protected eventLabel(e: AuditEventResource): string {
    return `${categoryLabel(e.category)} › ${actionLabel(e.action)}`;
  }

  protected outcomeText(e: AuditEventResource): string {
    return outcomeLabel(e.outcome);
  }

  protected resource(e: AuditEventResource): string {
    return resourceText(e);
  }

  protected details(e: AuditEventResource) {
    return detailRows(e);
  }

  protected searchTextOf(e: AuditEventResource): string | null {
    const text = e.searchText;
    if (!text) return null;
    return Object.values(text).filter(Boolean).join('\n') || null;
  }

  protected short(id: string): string {
    return shortId(id);
  }

  protected date(value: unknown): string {
    return value ? this.dates().format(new Date(String(value))) : '';
  }

  protected num(value: unknown): string {
    return value === null || value === undefined ? '' : this.numbers().format(Number(value));
  }

  protected onlyUser(e: AuditEventResource): void {
    this.setFilter({ actor: e.actor.id, actorName: e.actor.displayName });
  }

  protected onlyDocument(e: AuditEventResource): void {
    if (e.resource?.controlNumber) this.setFilter({ controlNumber: e.resource.controlNumber });
  }

  protected onlyJob(e: AuditEventResource): void {
    if (e.jobId) this.setFilter({ job: e.jobId });
  }

  protected fieldError(key: string): string | undefined {
    return this.invalid()[key]?.[0];
  }

  private async loadActions(): Promise<void> {
    try {
      this.actions.set(await this.api.actions());
    } catch {
      // The filters still work without the lists; the API validates what is typed into the URL.
    }
  }

  private async loadExports(): Promise<void> {
    try {
      const page = await this.api.exports();
      this.exports.set(page.items);
      if (page.items.some(isActiveExport)) this.schedulePoll();
    } catch {
      this.exports.set([]);
    }
  }

  private schedulePoll(): void {
    this.stopPolling();
    this.poll = setTimeout(() => void this.refreshActive(), AUDIT_EXPORT_POLL_MS);
  }

  private stopPolling(): void {
    if (this.poll !== null) clearTimeout(this.poll);
    this.poll = null;
  }

  private async refreshActive(): Promise<void> {
    this.poll = null;
    const active = this.exports().filter(isActiveExport);
    for (const e of active) {
      try {
        const current = await this.api.export(e.exportId);
        this.exports.update((list) =>
          list.map((x) => (x.exportId === current.exportId ? current : x)),
        );
        if (current.status === 'completed') {
          this.toasts.show('Audit export completed. The CSV is ready to download.', {
            tone: 'success',
          });
        } else if (current.status === 'failed') {
          this.toasts.show(`Audit export failed: ${current.statusReason ?? 'run it again.'}`, {
            tone: 'error',
          });
        }
      } catch {
        // Checked again on the next round.
      }
    }
    if (this.exports().some(isActiveExport)) this.schedulePoll();
  }
}
