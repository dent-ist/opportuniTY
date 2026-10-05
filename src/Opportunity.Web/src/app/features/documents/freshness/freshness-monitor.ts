import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { toApiError } from '../../../core/api/problem-details';
import {
  type IndexFreshness,
  type ServedFreshness,
  SearchFreshnessApi,
  announcementText,
  fromServed,
} from '../../../core/search/search-freshness';
import { Announcer } from '../../../ui';

/** How often the index state is read while it is not current (wave-10 contract: cheap enough for every 5 s). */
export const FRESHNESS_POLL_MS = new InjectionToken<number>('FRESHNESS_POLL_MS', {
  factory: () => 5000,
});
/** Freshness changes are announced politely at most once per this interval (E16-T07). */
export const FRESHNESS_ANNOUNCE_MS = new InjectionToken<number>('FRESHNESS_ANNOUNCE_MS', {
  factory: () => 30_000,
});

/**
 * The search index's freshness for the Documents page (E16-T07, Q-10): seeded by every result set the list is
 * served, then read from `GET …/search-freshness` every few seconds while the index is not current or while one of
 * the reviewer's jobs is catching up. When the state changes it is announced politely, at most once per 30 s (the
 * latest state wins). Polling stops for good when the endpoint is not there (404) or not allowed (403); the pill
 * then follows the served results only. Provided by the Documents page.
 */
@Injectable()
export class FreshnessMonitor {
  private readonly api = inject(SearchFreshnessApi, { optional: true });
  private readonly announcer = inject(Announcer);
  private readonly pollMs = inject(FRESHNESS_POLL_MS);
  private readonly announceMs = inject(FRESHNESS_ANNOUNCE_MS);

  private readonly _index = signal<IndexFreshness | null>(null);
  /** The index now (as last read), or null before the first result set. */
  readonly index = this._index.asReadonly();
  private readonly _served = signal<ServedFreshness | null>(null);
  /** The freshness the list's current result set was served with. */
  readonly served = this._served.asReadonly();

  private following = false;
  private unavailable = false;
  private destroyed = false;
  private polling = false;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer) clearTimeout(this.timer);
    });
  }

  /** A result set was served (a search ran, or a page was read). */
  observe(served: ServedFreshness): void {
    this._served.set(served);
    const index = this._index();
    // A newer reading of the index is not overridden by an older result set.
    if (!index || !(Date.parse(index.asOf) > Date.parse(served.asOf))) this.set(fromServed(served));
    this.schedule();
  }

  /** Keep reading while a job of the reviewer's is catching up (the banner), even if the last reading was current. */
  follow(on: boolean): void {
    this.following = on;
    if (on) this.schedule();
  }

  private set(next: IndexFreshness): void {
    const prev = this._index();
    this._index.set(next);
    if (prev && prev.state !== next.state) {
      this.announcer.announce(announcementText(next.state), {
        throttleKey: 'search-freshness',
        minIntervalMs: this.announceMs,
      });
    }
  }

  private needed(): boolean {
    const index = this._index();
    return this.following || (index !== null && index.state !== 'current');
  }

  private schedule(): void {
    if (!this.api || this.destroyed || this.unavailable || this.timer || this.polling) return;
    if (!this.needed()) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.poll();
    }, this.pollMs);
  }

  private async poll(): Promise<void> {
    if (!this.api || this.destroyed || !this.needed()) return;
    this.polling = true;
    try {
      const next = await this.api.get();
      if (this.destroyed) return;
      this.set(next);
    } catch (e) {
      const status = toApiError(e).status;
      // Not deployed yet, or no access: the served results keep the pill up to date.
      if (status === 403 || status === 404) this.unavailable = true;
    } finally {
      this.polling = false;
    }
    this.schedule();
  }
}
