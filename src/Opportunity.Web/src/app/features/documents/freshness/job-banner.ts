import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Announcer, Icon, IconButton } from '../../../ui';
import { FreshnessMonitor } from './freshness-monitor';
import { PendingSearchJobs, bannerText } from './pending-jobs';

/**
 * Banner of the Documents list while a job the reviewer started is saved but not yet searchable (E16-T07): "Mass Edit
 * saved · Search index updating… (63%)", with a link to the job page. It clears once the index has caught up with the
 * job (the job's Searchable phase is current); appearing and clearing are announced politely.
 */
@Component({
  selector: 'opp-search-job-banner',
  imports: [Icon, IconButton, RouterLink],
  template: `@for (job of items(); track job.jobId) {
    <div class="jb" role="group" [attr.aria-label]="job.title + ' search progress'">
      <opp-icon name="refresh" />
      <span class="jb__text">{{ job.text }}</span>
      <a class="jb__link" [routerLink]="['/w', workspaceId, 'jobs', job.jobId]">View job</a>
      <button
        type="button"
        oppIconButton
        [label]="'Hide ' + job.title + ' progress'"
        (click)="pending.dismiss(job.jobId)"
      >
        <opp-icon name="close" />
      </button>
    </div>
  }`,
  styleUrl: './job-banner.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'jb-list' },
})
export class SearchJobBanner {
  protected readonly pending = inject(PendingSearchJobs);
  private readonly monitor = inject(FreshnessMonitor);
  private readonly prefs = inject(UiPreferences);
  private readonly announcer = inject(Announcer);
  protected readonly workspaceId = inject(WorkspaceContext).workspaceId;

  protected readonly items = computed(() => {
    const locale = this.prefs.locale();
    return this.pending.jobs().map((j) => ({ ...j, text: bannerText(j, locale) }));
  });

  constructor() {
    let shown = new Map<string, string>();
    effect(() => {
      const jobs = this.pending.jobs();
      untracked(() => {
        this.monitor.follow(jobs.length > 0);
        const next = new Map(jobs.map((j) => [j.jobId, j.title]));
        for (const [id, title] of next) {
          if (!shown.has(id)) this.announce(`${title} saved. The search index is updating.`);
        }
        for (const [id, title] of shown) {
          if (!next.has(id) && !this.pending.wasDismissed(id))
            this.announce(`${title} is now searchable.`);
        }
        shown = next;
      });
    });
  }

  private announce(message: string): void {
    this.announcer.announce(message, { throttleKey: 'search-job-banner', minIntervalMs: 2000 });
  }
}
