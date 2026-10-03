import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Icon, IconName } from '../icon/icon';

// Data-state indicators (ADR-018 §7). Every state is carried by text (and usually an icon), never by
// colour alone (WCAG 1.4.1); colour only reinforces it.

export type BadgeTone =
  'neutral' | 'info' | 'success' | 'warning' | 'danger' | 'privileged' | 'aeo' | 'restricted';

/** Static label. Not interactive. */
@Component({
  selector: 'opp-badge',
  imports: [Icon],
  template: `@if (icon(); as name) {
      <opp-icon [name]="name" />
    }
    <ng-content />`,
  styleUrl: './badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': `'badge badge--' + tone()` },
})
export class Badge {
  readonly tone = input<BadgeTone>('neutral');
  readonly icon = input<IconName>();
}

export type Protection = 'privileged' | 'aeo' | 'confidential' | 'restricted';

const PROTECTION: Record<
  Protection,
  { tone: BadgeTone; icon: IconName; short: string; full: string }
> = {
  privileged: { tone: 'privileged', icon: 'lock', short: 'Privileged', full: 'Privileged' },
  aeo: { tone: 'aeo', icon: 'lock', short: 'AEO', full: "Attorneys' Eyes Only" },
  confidential: { tone: 'warning', icon: 'lock', short: 'Confidential', full: 'Confidential' },
  restricted: {
    tone: 'restricted',
    icon: 'eye-off',
    short: 'Access restricted',
    full: 'Access restricted',
  },
};

/**
 * Privilege / confidentiality designation of a document, and the "Access restricted" state shown for a
 * stale search hit the authoritative check denied (ui-ux finding 7).
 */
@Component({
  selector: 'opp-protection-badge',
  imports: [Badge],
  template: `<opp-badge [tone]="spec().tone" [icon]="spec().icon">
    @if (spec().short !== spec().full) {
      <abbr [title]="spec().full" aria-hidden="true">{{ spec().short }}</abbr>
      <span class="opp-visually-hidden">{{ spec().full }}</span>
    } @else {
      {{ spec().short }}
    }
  </opp-badge>`,
  styleUrl: './badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'badge-host' },
})
export class ProtectionBadge {
  readonly kind = input.required<Protection>();
  protected readonly spec = computed(() => PROTECTION[this.kind()]);
}

/** Search projection freshness in plain language (Q-10); raw generations are for admins only. */
export type Freshness = 'current' | 'updating' | 'delayed';

const FRESHNESS: Record<Freshness, { tone: BadgeTone; icon: IconName }> = {
  current: { tone: 'success', icon: 'success' },
  updating: { tone: 'info', icon: 'refresh' },
  delayed: { tone: 'warning', icon: 'clock' },
};

@Component({
  selector: 'opp-freshness',
  imports: [Badge],
  template: `<opp-badge [tone]="tone()" [icon]="icon()">{{ text() }}</opp-badge>`,
  styleUrl: './badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'badge-host' },
})
export class FreshnessIndicator {
  readonly state = input.required<Freshness>();
  /** Changes not yet searchable (approximate). */
  readonly pending = input<number>();
  /** When the served results were current (ISO-8601 or Date). */
  readonly asOf = input<string | Date>();

  private readonly prefs = inject(UiPreferences);

  protected readonly tone = computed(() => FRESHNESS[this.state()].tone);
  protected readonly icon = computed(() => FRESHNESS[this.state()].icon);
  protected readonly text = computed(() => {
    const locale = this.prefs.locale();
    const asOf = this.asOf();
    const time = asOf
      ? new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit' }).format(
          new Date(asOf),
        )
      : null;
    switch (this.state()) {
      case 'current':
        return time ? `Current as of ${time}` : 'Current';
      case 'updating': {
        const pending = this.pending();
        return pending
          ? `Updating · ≈ ${new Intl.NumberFormat(locale).format(pending)} changes pending`
          : 'Updating';
      }
      case 'delayed':
        return time ? `Delayed · results as of ${time}` : 'Delayed · results may be out of date';
    }
  });
}

/**
 * A document count with its precision made explicit (ADR-019 `total.relation`, Q-10, Q-32):
 * `eq` "12,400", `gte` "≥ 10,000", `approx` "≈ 12,400" (stale while the index catches up).
 */
@Component({
  selector: 'opp-count',
  template: `<span aria-hidden="true">{{ visual() }}</span
    ><span class="opp-visually-hidden">{{ spoken() }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'count' },
})
export class Count {
  readonly value = input.required<number>();
  readonly relation = input<'eq' | 'gte' | 'approx'>('eq');

  private readonly prefs = inject(UiPreferences);
  private readonly formatted = computed(() =>
    new Intl.NumberFormat(this.prefs.locale()).format(this.value()),
  );
  protected readonly visual = computed(
    () => ({ eq: '', gte: '≥ ', approx: '≈ ' })[this.relation()] + this.formatted(),
  );
  protected readonly spoken = computed(
    () =>
      ({ eq: '', gte: 'at least ', approx: 'approximately ' })[this.relation()] + this.formatted(),
  );
}

export type JobStatus = 'queued' | 'running' | 'succeeded' | 'partial' | 'failed' | 'cancelled';

const JOB: Record<JobStatus, { tone: BadgeTone; icon: IconName; text: string }> = {
  queued: { tone: 'neutral', icon: 'clock', text: 'Queued' },
  running: { tone: 'info', icon: 'refresh', text: 'Running' },
  succeeded: { tone: 'success', icon: 'success', text: 'Completed' },
  partial: { tone: 'warning', icon: 'warning', text: 'Completed with errors' },
  failed: { tone: 'danger', icon: 'error', text: 'Failed' },
  cancelled: { tone: 'neutral', icon: 'close', text: 'Cancelled' },
};

/** Job / workflow status pill. */
@Component({
  selector: 'opp-status-pill',
  imports: [Badge],
  template: `<opp-badge [tone]="spec().tone" [icon]="spec().icon">{{ spec().text }}</opp-badge>`,
  styleUrl: './badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'badge-host' },
})
export class StatusPill {
  readonly status = input.required<JobStatus>();
  protected readonly spec = computed(() => JOB[this.status()]);
}
