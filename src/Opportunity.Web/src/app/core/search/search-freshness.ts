import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { WorkspaceContext } from '../workspace/workspace-context';

// Search freshness (wave-10 contract "Search freshness", owned by #70) as a port, written against the contract while
// the API lands: the additions to `SearchFreshness` on every search response and page, and
// `GET …/search-freshness`. Readers are tolerant (int64 as strings, fields missing on an older API), so the
// Documents page works against the M1 shape `{ servedGeneration, current, asOf }` too. Wording rules (Q-10): reviewers
// see plain language only; raw generations and lag are for admins and support.

/** `delayed`: the oldest change not yet searchable is older than two minutes. */
export type FreshnessState = 'current' | 'updating' | 'delayed';

/** When the index counts as delayed (contract: oldest unreflected work older than 2 min). */
export const DELAYED_AFTER_SECONDS = 120;

/** The index's state now (`GET …/search-freshness`). */
export interface IndexFreshness {
  readonly state: FreshnessState;
  /** Refresh-aware watermark: every change up to this generation is searchable. */
  readonly indexedThroughGeneration: string | null;
  /** Newest committed generation (null when only a search response was seen). */
  readonly latestGeneration: string | null;
  /** Changes not yet searchable (may be approximate). */
  readonly pendingChanges: number;
  /** Age of the oldest change not yet searchable; 0 when current. */
  readonly lagSeconds: number;
  readonly asOf: string;
}

/** The freshness one result set was served with (`SearchResultPage.freshness`). */
export interface ServedFreshness extends Omit<IndexFreshness, 'latestGeneration'> {
  /** The generation the results reflect. */
  readonly servedGeneration: string | null;
}

type Raw = Record<string, unknown>;
const obj = (v: unknown): Raw => (v && typeof v === 'object' ? (v as Raw) : {});
const gen = (v: unknown): string | null =>
  v === null || v === undefined || v === '' ? null : String(v);
const count = (v: unknown): number => {
  const n = Number(v ?? 0);
  return Number.isFinite(n) && n > 0 ? n : 0;
};
const STATES = new Set<string>(['current', 'updating', 'delayed']);

function stateOf(raw: Raw): FreshnessState {
  const state = raw['state'];
  if (typeof state === 'string' && STATES.has(state)) return state as FreshnessState;
  // The M1 shape has only `current`; unknown means not known to be current.
  if (raw['current'] === true) return 'current';
  return count(raw['lagSeconds']) > DELAYED_AFTER_SECONDS ? 'delayed' : 'updating';
}

/** Reads `SearchResultPage.freshness`. */
export function toServedFreshness(body: unknown): ServedFreshness {
  const raw = obj(body);
  const state = stateOf(raw);
  return {
    state,
    servedGeneration: gen(raw['servedGeneration']),
    indexedThroughGeneration: gen(raw['indexedThroughGeneration']),
    pendingChanges: state === 'current' ? 0 : count(raw['pendingChanges']),
    lagSeconds: state === 'current' ? 0 : count(raw['lagSeconds']),
    asOf: String(raw['asOf'] ?? ''),
  };
}

/** Reads `GET …/search-freshness`. */
export function toIndexFreshness(body: unknown): IndexFreshness {
  const raw = obj(body);
  const state = stateOf(raw);
  return {
    state,
    indexedThroughGeneration: gen(raw['indexedThroughGeneration']),
    latestGeneration: gen(raw['latestGeneration']),
    pendingChanges: state === 'current' ? 0 : count(raw['pendingChanges']),
    lagSeconds: state === 'current' ? 0 : count(raw['lagSeconds']),
    asOf: String(raw['asOf'] ?? new Date().toISOString()),
  };
}

/** The index state a served result set implies (until the endpoint answers). */
export function fromServed(served: ServedFreshness): IndexFreshness {
  return {
    state: served.state,
    indexedThroughGeneration: served.indexedThroughGeneration ?? served.servedGeneration,
    latestGeneration: null,
    pendingChanges: served.pendingChanges,
    lagSeconds: served.lagSeconds,
    asOf: served.asOf,
  };
}

@Injectable()
export abstract class SearchFreshnessApi {
  /** `GET /api/v1/workspaces/{ws}/search-freshness` (needs `Search.Execute`). */
  abstract get(): Promise<IndexFreshness>;
}

@Injectable()
export class HttpSearchFreshnessApi extends SearchFreshnessApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  get(): Promise<IndexFreshness> {
    return firstValueFrom(
      this.http.get<unknown>(this.context.apiUrl('search-freshness')).pipe(map(toIndexFreshness)),
    );
  }
}

// ── Wording ──────────────────────────────────────────────────────────────────────────────────────────────────

/** "~12 s behind", "~3 min behind", "~2 h behind" (rounded; never a bare number). */
export function lagText(seconds: number): string {
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))} s behind`;
  if (seconds < 5400) return `~${Math.round(seconds / 60)} min behind`;
  return `~${Math.round(seconds / 3600)} h behind`;
}

/** The pill: "Current", "Updating · ≈ 1,240 changes pending · ~12 s behind", "Delayed · ~5 min behind". */
export function pillText(
  f: Pick<IndexFreshness, 'state' | 'pendingChanges' | 'lagSeconds'>,
  locale: string,
): string {
  switch (f.state) {
    case 'current':
      return 'Current';
    case 'updating': {
      const parts = ['Updating'];
      if (f.pendingChanges > 0)
        parts.push(`≈ ${new Intl.NumberFormat(locale).format(f.pendingChanges)} changes pending`);
      if (f.lagSeconds > 0) parts.push(lagText(f.lagSeconds));
      return parts.join(' · ');
    }
    case 'delayed':
      return f.lagSeconds > DELAYED_AFTER_SECONDS
        ? `Delayed · ${lagText(f.lagSeconds)}`
        : 'Delayed · more than 2 min behind';
  }
}

/** What a screen reader hears when the state changes (politely, at most once per 30 s). */
export function announcementText(state: FreshnessState): string {
  switch (state) {
    case 'current':
      return 'Search index is current. New searches include every saved change.';
    case 'updating':
      return 'Search index is updating. Counts may not include recent changes.';
    case 'delayed':
      return 'Search index is delayed. Recent changes are taking more than 2 minutes to become searchable.';
  }
}

/** Footnote under a result set that is not current: "Counts may not include 85 recent changes." */
export function footnoteText(pendingChanges: number, locale: string): string {
  if (pendingChanges <= 0) return 'Counts may not include recent changes.';
  const n = new Intl.NumberFormat(locale).format(pendingChanges);
  return `Counts may not include ${n} recent change${pendingChanges === 1 ? '' : 's'}.`;
}

/** Generations as admins read them: "18,432". */
export function generationText(generation: string | null, locale: string): string {
  if (generation === null) return '—';
  const n = Number(generation);
  return Number.isSafeInteger(n) ? new Intl.NumberFormat(locale).format(n) : generation;
}
