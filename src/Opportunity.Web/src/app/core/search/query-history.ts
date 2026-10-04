import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../api/generated/api-configuration';
import { listQueryHistory } from '../api/generated/fn/search/list-query-history';
import { recordQueryHistory } from '../api/generated/fn/search/record-query-history';
import { WorkspaceContext } from '../workspace/workspace-context';

export interface QueryHistoryEntry {
  readonly query: string;
  /** ISO 8601 time of the last run. */
  readonly ranAt: string;
}

/**
 * Durable per-user, per-workspace query history (#186): last 50 distinct valid queries, newest first, kept by the
 * server so it survives sign-out and other browsers. Query text is workspace data (Q-16), so it is never written to
 * browser storage.
 */
@Injectable({ providedIn: 'root', useFactory: () => inject(HttpQueryHistoryBackend) })
export abstract class QueryHistoryBackend {
  abstract load(workspaceId: string): Promise<readonly QueryHistoryEntry[]>;
  abstract record(workspaceId: string, entry: QueryHistoryEntry): Promise<void>;
}

/** `GET`/`POST /api/v1/workspaces/{workspaceId}/query-history`; the server stamps its own run time. */
@Injectable({ providedIn: 'root' })
export class HttpQueryHistoryBackend extends QueryHistoryBackend {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;

  async load(workspaceId: string): Promise<readonly QueryHistoryEntry[]> {
    const response = await firstValueFrom(
      listQueryHistory(this.http, this.rootUrl, { workspaceId }),
    );
    return response.body.items.map((e) => ({ query: e.query, ranAt: String(e.ranAt) }));
  }

  async record(workspaceId: string, entry: QueryHistoryEntry): Promise<void> {
    await firstValueFrom(
      recordQueryHistory(this.http, this.rootUrl, { workspaceId, body: { query: entry.query } }),
    );
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
