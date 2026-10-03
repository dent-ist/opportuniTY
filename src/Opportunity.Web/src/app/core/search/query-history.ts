import { Injectable, inject, signal } from '@angular/core';
import { WorkspaceContext } from '../workspace/workspace-context';

export interface QueryHistoryEntry {
  readonly query: string;
  /** ISO 8601 time of the last run. */
  readonly ranAt: string;
}

/**
 * Durable per-user query history. There is no API for it yet, so the default keeps nothing beyond the
 * workspace visit; a server-backed implementation (per user, per workspace, last 50) replaces it through DI.
 * Query text is workspace data, so it is never written to browser storage.
 */
@Injectable({ providedIn: 'root', useFactory: () => new NoQueryHistoryBackend() })
export abstract class QueryHistoryBackend {
  abstract load(workspaceId: string): Promise<readonly QueryHistoryEntry[]>;
  abstract record(workspaceId: string, entry: QueryHistoryEntry): Promise<void>;
}

export class NoQueryHistoryBackend extends QueryHistoryBackend {
  load(): Promise<readonly QueryHistoryEntry[]> {
    return Promise.resolve([]);
  }

  record(): Promise<void> {
    return Promise.resolve();
  }
}

/**
 * The signed-in user's recent queries in the current workspace, newest first, at most 50 and without
 * duplicates (re-running a query moves it to the top). Workspace-scoped: provided on the workspace shell,
 * so a workspace switch discards it (ADR-018 §2.3).
 */
@Injectable()
export class QueryHistory {
  static readonly MAX = 50;
  private readonly backend = inject(QueryHistoryBackend);
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;
  private readonly _entries = signal<readonly QueryHistoryEntry[]>([]);
  private loaded?: Promise<void>;

  readonly entries = this._entries.asReadonly();

  /** Loads the stored history once; later calls reuse the first load. */
  load(): Promise<void> {
    this.loaded ??= this.backend.load(this.workspaceId).then(
      (stored) => this._entries.update((current) => merge(current, stored)),
      () => undefined, // History is a convenience: a failed load leaves the session's own entries.
    );
    return this.loaded;
  }

  record(query: string, ranAt = new Date()): void {
    const text = query.trim();
    if (!text) return;
    const entry: QueryHistoryEntry = { query: text, ranAt: ranAt.toISOString() };
    this._entries.update((current) => merge([entry], current));
    this.backend.record(this.workspaceId, entry).catch(() => undefined);
  }
}

function merge(
  newer: readonly QueryHistoryEntry[],
  older: readonly QueryHistoryEntry[],
): QueryHistoryEntry[] {
  const seen = new Set<string>();
  const result: QueryHistoryEntry[] = [];
  for (const entry of [...newer, ...older]) {
    if (seen.has(entry.query)) continue;
    seen.add(entry.query);
    result.push(entry);
    if (result.length === QueryHistory.MAX) break;
  }
  return result;
}
