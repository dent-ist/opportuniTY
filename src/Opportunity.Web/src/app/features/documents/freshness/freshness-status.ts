import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { JOB_PERMISSIONS } from '../../../core/jobs/job-model';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { generationText } from '../../../core/search/search-freshness';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { FreshnessIndicator, Icon } from '../../../ui';
import { formatTime } from '../grid/grid-format';
import { FreshnessMonitor } from './freshness-monitor';
import { PendingSearchJobs } from './pending-jobs';

let nextId = 0;

/**
 * The freshness pill of the Documents list (E16-T07, Q-10): Current / Updating (≈ N changes pending, ~T s behind) /
 * Delayed, always as text with an icon (colour only reinforces it). Workspace admins and `Job.ViewAll` holders (the
 * support role) can open the detail with the raw generations and lag; reviewers never see generation numbers.
 */
@Component({
  selector: 'opp-freshness-status',
  imports: [FreshnessIndicator, Icon],
  templateUrl: './freshness-status.html',
  styleUrl: './freshness-status.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'fs',
    '(document:click)': 'onDocumentClick($event)',
    '(keydown.escape)': 'close(true)',
  },
})
export class FreshnessStatus {
  protected readonly monitor = inject(FreshnessMonitor);
  private readonly pending = inject(PendingSearchJobs, { optional: true });
  private readonly prefs = inject(UiPreferences);
  private readonly context = inject(WorkspaceContext);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly toggleButton = viewChild<ElementRef<HTMLButtonElement>>('toggleRef');

  /** Admin and support roles (Q-10, ticket review E16-T07). */
  protected readonly canSeeDetails =
    this.context.can(JOB_PERMISSIONS.viewAll) || this.context.can(PERMISSIONS.manageUsers);
  protected readonly panelId = `freshness-detail-${++nextId}`;
  protected readonly open = signal(false);

  protected readonly index = this.monitor.index;
  protected readonly detail = computed(() => {
    const index = this.index();
    if (!index) return null;
    const locale = this.prefs.locale();
    const g = (v: string | null) => generationText(v, locale);
    const served = this.monitor.served();
    const jobs = (this.pending?.jobs() ?? []).filter((j) => j.jobGeneration !== null);
    return {
      through: g(index.indexedThroughGeneration),
      latest: index.latestGeneration === null ? null : g(index.latestGeneration),
      jobs: jobs.map((j) => ({ title: j.title, generation: g(j.jobGeneration) })),
      served: served ? g(served.servedGeneration) : null,
      pending: new Intl.NumberFormat(locale).format(index.pendingChanges),
      lag: `${new Intl.NumberFormat(locale).format(Math.round(index.lagSeconds))} s`,
      checked: formatTime(index.asOf, locale, this.context.workspace.displayTimeZone || 'UTC'),
    };
  });

  protected toggle(): void {
    this.open.update((o) => !o);
  }

  protected close(returnFocus = false): void {
    if (!this.open()) return;
    this.open.set(false);
    if (returnFocus) this.toggleButton()?.nativeElement.focus();
  }

  protected onDocumentClick(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.close();
  }
}
