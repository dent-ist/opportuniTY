import type { Route } from '@playwright/test';

/**
 * Search freshness (wave-10 contract "Search freshness", #70) for the e2e mock: the additions to every search
 * response's `freshness` and `GET …/search-freshness`. Tests move the index between states on the "server"
 * (`updating()`, `delayed()`, `catchUp()`); the page follows by polling while it is not current.
 */
export type MockFreshnessState = 'current' | 'updating' | 'delayed';

export interface MockFreshness {
  state: MockFreshnessState;
  indexedThroughGeneration: number;
  latestGeneration: number;
  pendingChanges: number;
  lagSeconds: number;
}

const CURRENT: MockFreshness = {
  state: 'current',
  indexedThroughGeneration: 18432,
  latestGeneration: 18432,
  pendingChanges: 0,
  lagSeconds: 0,
};

export class FreshnessMock {
  /** `GET …/search-freshness` requests received. */
  polls = 0;
  private value: MockFreshness;

  constructor(initial: MockFreshnessState = 'current') {
    this.value = { ...CURRENT };
    if (initial === 'updating') this.updating();
    if (initial === 'delayed') this.delayed();
  }

  get current(): MockFreshness {
    return this.value;
  }

  /** Recent changes are saved but not yet searchable. */
  updating(pendingChanges = 85, lagSeconds = 12): void {
    this.value = {
      state: 'updating',
      indexedThroughGeneration: 18432,
      latestGeneration: 18517,
      pendingChanges,
      lagSeconds,
    };
  }

  /** The oldest change not yet searchable is older than two minutes. */
  delayed(pendingChanges = 1240, lagSeconds = 185): void {
    this.value = {
      state: 'delayed',
      indexedThroughGeneration: 18432,
      latestGeneration: 18517,
      pendingChanges,
      lagSeconds,
    };
  }

  /** The index has caught up with everything saved. */
  catchUp(): void {
    this.value = {
      ...CURRENT,
      indexedThroughGeneration: this.value.latestGeneration,
      latestGeneration: this.value.latestGeneration,
    };
  }

  /** `SearchResultPage.freshness` of a result set served now. */
  served(): Record<string, unknown> {
    const v = this.value;
    return {
      // While current, the fixed time the other scenarios have always seen.
      asOf: v.state === 'current' ? '2026-10-03T10:42:00Z' : new Date().toISOString(),
      current: v.state === 'current',
      servedGeneration: v.indexedThroughGeneration,
      state: v.state,
      indexedThroughGeneration: v.indexedThroughGeneration,
      pendingChanges: v.pendingChanges,
      lagSeconds: v.lagSeconds,
    };
  }

  /** Answers `GET …/search-freshness`, or returns undefined. */
  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    if (method !== 'GET' || !/^\/api\/v1\/workspaces\/[^/]+\/search-freshness$/.test(path))
      return undefined;
    this.polls++;
    const v = this.value;
    return route.fulfill({
      json: {
        state: v.state,
        indexedThroughGeneration: v.indexedThroughGeneration,
        latestGeneration: v.latestGeneration,
        pendingChanges: v.pendingChanges,
        lagSeconds: v.lagSeconds,
        asOf: new Date().toISOString(),
      },
    });
  }
}
