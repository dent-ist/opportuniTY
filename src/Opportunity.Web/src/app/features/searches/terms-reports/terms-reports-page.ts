import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { SessionService } from '../../../core/session/session';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import {
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  MENU,
  StatusPill,
  ToastService,
} from '../../../ui';
import { formatDate } from '../saved-search-model';
import {
  HttpSearchTermReportApi,
  SearchTermReportApi,
  SearchTermReportSummary,
} from './search-term-report-api';
import { canDelete, reportErrorText, scopeLabel, statusPill } from './search-term-report-model';

/**
 * Searches › Search Terms Reports (#180, familiarity guide §2.3): the reports of the workspace the caller may see,
 * newest first, with scope, number of terms, status and who ran them; New report, open, and delete (the person who
 * ran it, or an admin). Each report runs as a background job on a frozen set, so its numbers can be reproduced.
 */
@Component({
  selector: 'opp-terms-reports-page',
  imports: [
    Button,
    EmptyState,
    ErrorState,
    Icon,
    IconButton,
    LoadingState,
    RouterLink,
    StatusPill,
    ...MENU,
  ],
  templateUrl: './terms-reports-page.html',
  styleUrl: './terms-reports.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: SearchTermReportApi, useClass: HttpSearchTermReportApi }],
})
export class TermsReportsPage {
  private readonly api = inject(SearchTermReportApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly me = inject(SessionService).principal;

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly canCreate = this.context.can(PERMISSIONS.searchExecute);
  protected readonly items = signal<readonly SearchTermReportSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';

  protected readonly rows = computed(() => {
    const locale = this.prefs.locale();
    const n = new Intl.NumberFormat(locale);
    const caller = { userId: this.me()?.userId ?? null, can: (p: string) => this.context.can(p) };
    return this.items().map((r) => ({
      r,
      scope: scopeLabel(r.scope),
      terms: n.format(r.termCount),
      by: r.createdBy.userId === caller.userId ? 'You' : r.createdBy.displayName,
      created: formatDate(r.createdAt, locale, this.timeZone),
      completed: r.completedAt ? formatDate(r.completedAt, locale, this.timeZone) : '—',
      pill: statusPill(r.status),
      canDelete: canDelete(r, caller),
    }));
  });

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

  protected open(r: SearchTermReportSummary): void {
    void this.router.navigate(['/w', this.workspaceId, 'searches', 'terms-reports', r.reportId]);
  }

  protected async remove(r: SearchTermReportSummary): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: 'Delete Search Terms Report?',
      message: `“${r.name}” and its results will be deleted. Documents, their coding and the frozen set it counted are not changed.`,
      confirmLabel: 'Delete report',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await this.api.delete(r.reportId);
      this.toasts.show(`Search Terms Report “${r.name}” deleted.`, { tone: 'success' });
    } catch (e) {
      this.toasts.show(reportErrorText(toApiError(e)), { tone: 'error' });
    }
    await this.load();
  }
}
