import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { ApiError, UserFacingError, describeError } from '../../core/api/problem-details';
import { Button } from '../button/button';
import { Icon } from '../icon/icon';

// Loading / empty / error states (ADR-018 §7). Every async region renders exactly one of: content,
// <opp-loading-state>, <opp-empty-state> or <opp-error-state>.

/** Busy indicator for a region. Announced politely once; the region itself should carry aria-busy. */
@Component({
  selector: 'opp-loading-state',
  template: `<span class="state__spinner" aria-hidden="true"></span><span>{{ label() }}</span>`,
  styleUrl: './state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'state state--loading', role: 'status' },
})
export class LoadingState {
  readonly label = input('Loading…');
}

/** Nothing to show, with the reason and, where possible, the next step (projected actions). */
@Component({
  selector: 'opp-empty-state',
  imports: [Icon],
  template: `<opp-icon class="state__icon" name="inbox" />
    <h2 class="state__title">{{ title() }}</h2>
    @if (message()) {
      <p class="state__message">{{ message() }}</p>
    }
    <div class="state__actions"><ng-content /></div>`,
  styleUrl: './state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'state state--empty' },
})
export class EmptyState {
  readonly title = input.required<string>();
  readonly message = input<string>();
}

/**
 * A failed load. Takes an `ApiError` (normalised problem details) or a ready message; shows the trace id
 * as a support reference and offers Retry when retrying can help. Announced as an alert.
 */
@Component({
  selector: 'opp-error-state',
  imports: [Icon, Button],
  template: `<opp-icon class="state__icon" name="error" />
    <h2 class="state__title">{{ view().title }}</h2>
    <p class="state__message">{{ view().detail }}</p>
    @if (view().reference; as reference) {
      <p class="state__reference">
        Reference: <code>{{ reference }}</code>
      </p>
    }
    <div class="state__actions">
      @if (view().retryable) {
        <button type="button" oppButton="secondary" (click)="retry.emit()">Try again</button>
      }
      <ng-content />
    </div>`,
  styleUrl: './state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'state state--error', role: 'alert' },
})
export class ErrorState {
  readonly error = input.required<ApiError | UserFacingError>();
  readonly retry = output<void>();
  protected readonly view = computed(() => {
    const e = this.error();
    return e instanceof ApiError ? describeError(e) : e;
  });
}

/** Determinate or indeterminate progress bar with an accessible name. */
@Component({
  selector: 'opp-progress',
  template: `<div class="progress__track"><div class="progress__bar"></div></div>
    @if (showValue() && value() !== undefined) {
      <span class="progress__value" aria-hidden="true">{{ percentText() }}</span>
    }`,
  styleUrl: './state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'progress',
    role: 'progressbar',
    '[attr.aria-label]': 'label()',
    '[attr.aria-valuemin]': '0',
    '[attr.aria-valuemax]': 'max()',
    '[attr.aria-valuenow]': 'value() ?? null',
    '[attr.aria-valuetext]': 'value() === undefined ? null : percentText()',
    '[class.progress--indeterminate]': 'value() === undefined',
    '[style.--progress]': 'fraction()',
  },
})
export class Progress {
  readonly label = input.required<string>();
  /** Omit for indeterminate. */
  readonly value = input<number>();
  readonly max = input(100);
  readonly showValue = input(true);

  private readonly prefs = inject(UiPreferences);
  protected readonly fraction = computed(() => {
    const v = this.value();
    return v === undefined ? 0 : Math.min(1, Math.max(0, v / (this.max() || 1)));
  });
  protected readonly percentText = computed(() =>
    new Intl.NumberFormat(this.prefs.locale(), { style: 'percent' }).format(this.fraction()),
  );
}
