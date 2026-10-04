import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { JOB_POLL_MS, JobFeed } from '../../core/jobs/job-feed';
import { Icon } from '../../ui';

/** How the page is kept up to date: live, or re-read every few seconds while the live connection is down. */
@Component({
  selector: 'opp-live-note',
  imports: [Icon],
  template: `@if (text(); as t) {
    <opp-icon [name]="feed?.mode() === 'live' ? 'success' : 'refresh'" />{{ t }}
  }`,
  styleUrl: './live-note.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'live-note' },
})
export class LiveNote {
  protected readonly feed = inject(JobFeed, { optional: true });
  private readonly seconds = Math.round(inject(JOB_POLL_MS) / 1000);

  protected readonly text = computed(() => {
    switch (this.feed?.mode()) {
      case 'live':
        return 'Updates live';
      case 'polling':
        return `Updates every ${this.seconds} seconds`;
      default:
        return null;
    }
  });
}
