import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ActiveJobFeed } from '../core/jobs/job-feed';
import { phasesText, statusLabel, typeLabel, type JobSummary } from '../core/jobs/job-model';
import { UiPreferences } from '../core/preferences/ui-preferences';
import { Icon, MENU } from '../ui';

/**
 * The header's job tray (familiarity guide §2.2, E06-T07): how many of the user's jobs are running (every job's for
 * `Job.ViewAll` holders) and the most recent ones with their Saved / Searchable progress, each linking to its job
 * page, plus the Jobs page. Live through the workspace's `JobFeed`; outside a workspace it is not shown.
 */
@Component({
  selector: 'opp-job-tray',
  imports: [Icon, RouterLink, ...MENU],
  template: `@if (feed(); as f) {
    <button
      type="button"
      class="tray__trigger"
      [class.is-busy]="f.running() > 0"
      [cdkMenuTriggerFor]="menu"
      [attr.aria-label]="label()"
    >
      <opp-icon name="jobs" />
      <span class="tray__text">Jobs</span>
      @if (f.running() > 0) {
        <span class="tray__count">{{ f.running() }}</span>
      }
    </button>
    <ng-template #menu>
      <div cdkMenu class="opp-menu opp-menu--wide" aria-label="Recent jobs">
        <div class="opp-menu__heading" aria-hidden="true">
          {{ f.scope === 'all' ? 'Recent jobs in this workspace' : 'Your recent jobs' }}
        </div>
        @for (job of f.recent(); track job.jobId) {
          <a
            cdkMenuItem
            class="opp-menu__item opp-menu__item--stacked"
            [routerLink]="['/w', f.workspaceId, 'jobs', job.jobId]"
          >
            <span class="opp-menu__title">{{ type(job) }} · {{ job.name }}</span
            ><span class="opp-visually-hidden">: </span>
            <span class="opp-menu__subtext">{{ status(job) }} · {{ phases(job) }}</span>
          </a>
        } @empty {
          <p class="opp-menu__empty">No recent jobs.</p>
        }
        <div class="opp-menu__separator" role="separator"></div>
        <a cdkMenuItem class="opp-menu__item" [routerLink]="['/w', f.workspaceId, 'jobs']">
          All jobs
        </a>
      </div>
    </ng-template>
  }`,
  styleUrl: './job-tray.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobTray {
  protected readonly feed = inject(ActiveJobFeed).current;
  private readonly prefs = inject(UiPreferences);

  protected readonly label = computed(() => {
    const running = this.feed()?.running() ?? 0;
    return running ? `Jobs, ${running} running` : 'Jobs, none running';
  });

  protected type(job: JobSummary): string {
    return typeLabel(job.type);
  }

  protected status(job: JobSummary): string {
    return statusLabel(job.status);
  }

  protected phases(job: JobSummary): string {
    return phasesText(job, this.prefs.locale());
  }
}
