import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DestroyRef, Injectable, inject } from '@angular/core';

export interface AnnounceOptions {
  politeness?: 'polite' | 'assertive';
  /**
   * Throttle repeated announcements that share this key, e.g. freshness changes are announced at most once
   * per 30 s (ui-ux review, freshness indicator). The latest message wins when the window reopens.
   */
  throttleKey?: string;
  minIntervalMs?: number;
}

/**
 * Screen-reader announcements through one CDK live region (WCAG 4.1.3). Use for async outcomes the user
 * did not trigger on the focused control: job completion, freshness, selection counts, "results refreshed".
 */
@Injectable({ providedIn: 'root' })
export class Announcer {
  private readonly live = inject(LiveAnnouncer);
  private readonly lastAt = new Map<string, number>();
  private readonly pending = new Map<string, ReturnType<typeof setTimeout>>();

  constructor() {
    // A throttled message still waiting when the app (or a test) is torn down is dropped.
    inject(DestroyRef).onDestroy(() => this.pending.forEach(clearTimeout));
  }

  announce(message: string, options: AnnounceOptions = {}): void {
    const politeness = options.politeness ?? 'polite';
    const key = options.throttleKey;
    if (!key) {
      void this.live.announce(message, politeness);
      return;
    }
    const interval = options.minIntervalMs ?? 30_000;
    const since = Date.now() - (this.lastAt.get(key) ?? -Infinity);
    clearTimeout(this.pending.get(key));
    if (since >= interval) {
      this.lastAt.set(key, Date.now());
      void this.live.announce(message, politeness);
    } else {
      this.pending.set(
        key,
        setTimeout(() => {
          this.pending.delete(key);
          this.lastAt.set(key, Date.now());
          void this.live.announce(message, politeness);
        }, interval - since),
      );
    }
  }
}
