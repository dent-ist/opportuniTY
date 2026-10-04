import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { listSnapshots } from '../../core/api/generated/fn/snapshots/list-snapshots';
import { listWorkspaceMembers } from '../../core/api/generated/fn/workspaces/list-workspace-members';
import type { SnapshotResource } from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

// Saved searches (E07-T09 backend #71, E16-T11 UI) as a port. The routes are written by hand against the wave-9
// shared contract "Saved searches" until they are in the OpenAPI document; the e2e mock API
// (e2e/support/mock-saved-searches.ts) serves the same shapes. Frozen sets (`GET …/snapshots`) and the sharing
// candidates (`GET …/members`) use the generated client. Provided by the pages that use it, so it is
// workspace-scoped and replaceable in tests.

export type SavedSearchScope = 'private' | 'shared';
export type SharePrincipalKind = 'user' | 'group';

/** A user or IdP group a saved search is shared with. */
export interface SharePrincipal {
  readonly kind: SharePrincipalKind;
  readonly id: string;
  readonly displayName: string;
}

/** How current the last run's hit count was (Q-10). */
export interface SavedSearchFreshness {
  readonly state: 'current' | 'catchingUp';
  readonly asOf: string;
}

export interface SavedSearchSummary {
  readonly savedSearchId: string;
  readonly name: string;
  readonly folderId: string | null;
  readonly owner: { readonly userId: string; readonly displayName: string };
  readonly scope: SavedSearchScope;
  readonly sharedWith: readonly SharePrincipal[];
  readonly lastRunAt: string | null;
  readonly lastHitCount: number | null;
  readonly lastHitRelation: 'eq' | 'gte' | null;
  readonly lastRunFreshness: SavedSearchFreshness | null;
  readonly modifiedAt: string;
  readonly version: number;
}

export interface SavedSearchSortKey {
  readonly field: string;
  readonly direction: 'asc' | 'desc';
}

/** `SavedSearchResource`: the summary plus what runs. */
export interface SavedSearch extends SavedSearchSummary {
  readonly query: string;
  /** Field query names of the saved View columns; empty for the default view. */
  readonly columns: readonly string[];
  readonly sort: readonly SavedSearchSortKey[];
  readonly includeFamily: boolean;
  readonly astVersion: number | null;
}

/** Body of `POST /saved-searches` and `PUT /saved-searches/{id}`. */
export interface SavedSearchDraft {
  readonly name: string;
  readonly folderId: string | null;
  readonly query: string;
  readonly columns?: readonly string[];
  readonly sort?: readonly SavedSearchSortKey[];
  readonly includeFamily?: boolean;
}

export interface SavedSearchFolder {
  readonly folderId: string;
  readonly name: string;
  readonly parentFolderId: string | null;
  readonly version: number;
}

export interface SavedSearchPage {
  readonly items: readonly SavedSearchSummary[];
  readonly nextCursor: string | null;
}

export interface SavedSearchListQuery {
  readonly folderId?: string | null;
  readonly q?: string | null;
  readonly cursor?: string | null;
  readonly limit?: number;
}

/** A frozen set (snapshot) as the Searches section lists it: N documents at time T, by whom. */
export interface FrozenSetSummary {
  readonly snapshotId: string;
  readonly name: string;
  readonly purpose: string;
  readonly status: string;
  readonly documentCount: number | null;
  readonly frozenAt: string | null;
  readonly createdBy: string;
}

@Injectable()
export abstract class SavedSearchApi {
  /** `GET …/saved-search-folders`. */
  abstract folders(): Promise<readonly SavedSearchFolder[]>;
  /** `POST …/saved-search-folders`. */
  abstract createFolder(name: string, parentFolderId: string | null): Promise<SavedSearchFolder>;
  /** `PUT …/saved-search-folders/{id}` with If-Match: rename or move a folder. */
  abstract updateFolder(
    folder: SavedSearchFolder,
    name: string,
    parentFolderId: string | null,
  ): Promise<SavedSearchFolder>;
  /** `DELETE …/saved-search-folders/{id}`: only an empty folder (409 `FOLDER_NOT_EMPTY` otherwise). */
  abstract deleteFolder(folder: SavedSearchFolder): Promise<void>;
  /** `GET …/saved-searches`: the searches the caller may see (own, shared with them; Workspace Admins all). */
  abstract list(query?: SavedSearchListQuery): Promise<SavedSearchPage>;
  /** Every page of `list` up to `max` searches (the browser pane's tree). */
  abstract listAll(max?: number): Promise<readonly SavedSearchSummary[]>;
  /** `GET …/saved-searches/{id}`; 404 when it does not exist or is not visible to the caller. */
  abstract get(savedSearchId: string): Promise<SavedSearch>;
  /** `POST …/saved-searches`. */
  abstract create(draft: SavedSearchDraft): Promise<SavedSearch>;
  /** `PUT …/saved-searches/{id}` with If-Match (edit, rename, move). */
  abstract update(
    savedSearchId: string,
    version: number,
    draft: SavedSearchDraft,
  ): Promise<SavedSearch>;
  /** `DELETE …/saved-searches/{id}` with If-Match. */
  abstract delete(savedSearchId: string, version: number): Promise<void>;
  /** `POST …/saved-searches/{id}/clone`. */
  abstract clone(
    savedSearchId: string,
    name: string,
    folderId: string | null,
  ): Promise<SavedSearch>;
  /** `PUT …/saved-searches/{id}/sharing` (`SavedSearch.Share`); an empty list makes it private. */
  abstract share(
    savedSearchId: string,
    sharedWith: readonly Pick<SharePrincipal, 'kind' | 'id'>[],
  ): Promise<SavedSearch>;
  /** Users and groups a search can be shared with (`GET …/members`). */
  abstract shareCandidates(): Promise<readonly SharePrincipal[]>;
  /** `GET …/snapshots`: frozen sets, newest first (the caller's own; everyone's with Job.ViewAll). */
  abstract frozenSets(): Promise<readonly FrozenSetSummary[]>;
}

const FOLDERS = 'saved-search-folders';
const SEARCHES = 'saved-searches';

const ifMatch = (version: number) => ({ 'If-Match': `"${version}"` });

@Injectable()
export class HttpSavedSearchApi extends SavedSearchApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  async folders(): Promise<readonly SavedSearchFolder[]> {
    const page = await firstValueFrom(
      this.http.get<{ items: SavedSearchFolder[] }>(this.context.apiUrl(FOLDERS)),
    );
    return page.items.map(toFolder);
  }

  createFolder(name: string, parentFolderId: string | null): Promise<SavedSearchFolder> {
    return firstValueFrom(
      this.http
        .post<SavedSearchFolder>(this.context.apiUrl(FOLDERS), { name, parentFolderId })
        .pipe(map(toFolder)),
    );
  }

  updateFolder(
    folder: SavedSearchFolder,
    name: string,
    parentFolderId: string | null,
  ): Promise<SavedSearchFolder> {
    return firstValueFrom(
      this.http
        .put<SavedSearchFolder>(
          this.context.apiUrl(FOLDERS, folder.folderId),
          { name, parentFolderId },
          { headers: ifMatch(folder.version) },
        )
        .pipe(map(toFolder)),
    );
  }

  async deleteFolder(folder: SavedSearchFolder): Promise<void> {
    await firstValueFrom(
      this.http.delete(this.context.apiUrl(FOLDERS, folder.folderId), {
        headers: ifMatch(folder.version),
      }),
    );
  }

  list(query: SavedSearchListQuery = {}): Promise<SavedSearchPage> {
    const params: Record<string, string> = { limit: String(query.limit ?? 100) };
    if (query.folderId) params['folderId'] = query.folderId;
    if (query.q) params['q'] = query.q;
    if (query.cursor) params['cursor'] = query.cursor;
    return firstValueFrom(
      this.http.get<SavedSearchPage>(this.context.apiUrl(SEARCHES), { params }).pipe(
        map((page) => ({
          items: page.items.map(toSummary),
          nextCursor: page.nextCursor ?? null,
        })),
      ),
    );
  }

  async listAll(max = 1000): Promise<readonly SavedSearchSummary[]> {
    const items: SavedSearchSummary[] = [];
    let cursor: string | null = null;
    do {
      const page = await this.list({ cursor, limit: 200 });
      items.push(...page.items);
      cursor = page.nextCursor;
    } while (cursor && items.length < max);
    return items;
  }

  get(savedSearchId: string): Promise<SavedSearch> {
    return firstValueFrom(
      this.http.get<SavedSearch>(this.context.apiUrl(SEARCHES, savedSearchId)).pipe(map(toSaved)),
    );
  }

  create(draft: SavedSearchDraft): Promise<SavedSearch> {
    return firstValueFrom(
      this.http.post<SavedSearch>(this.context.apiUrl(SEARCHES), draft).pipe(map(toSaved)),
    );
  }

  update(savedSearchId: string, version: number, draft: SavedSearchDraft): Promise<SavedSearch> {
    return firstValueFrom(
      this.http
        .put<SavedSearch>(this.context.apiUrl(SEARCHES, savedSearchId), draft, {
          headers: ifMatch(version),
        })
        .pipe(map(toSaved)),
    );
  }

  async delete(savedSearchId: string, version: number): Promise<void> {
    await firstValueFrom(
      this.http.delete(this.context.apiUrl(SEARCHES, savedSearchId), { headers: ifMatch(version) }),
    );
  }

  clone(savedSearchId: string, name: string, folderId: string | null): Promise<SavedSearch> {
    return firstValueFrom(
      this.http
        .post<SavedSearch>(this.context.apiUrl(SEARCHES, savedSearchId, 'clone'), {
          name,
          folderId,
        })
        .pipe(map(toSaved)),
    );
  }

  share(
    savedSearchId: string,
    sharedWith: readonly Pick<SharePrincipal, 'kind' | 'id'>[],
  ): Promise<SavedSearch> {
    return firstValueFrom(
      this.http
        .put<SavedSearch>(this.context.apiUrl(SEARCHES, savedSearchId, 'sharing'), {
          sharedWith: sharedWith.map(({ kind, id }) => ({ kind, id })),
        })
        .pipe(map(toSaved)),
    );
  }

  async shareCandidates(): Promise<readonly SharePrincipal[]> {
    const page = await firstValueFrom(
      listWorkspaceMembers(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 500,
      }),
    );
    const seen = new Map<string, SharePrincipal>();
    for (const m of page.body.items) {
      const principal: SharePrincipal | null =
        m.kind === 'group'
          ? m.groupName
            ? { kind: 'group', id: m.groupName, displayName: m.groupName }
            : null
          : m.userId
            ? { kind: 'user', id: m.userId, displayName: m.displayName ?? m.userId }
            : null;
      if (principal) seen.set(`${principal.kind}:${principal.id}`, principal);
    }
    return [...seen.values()].sort(
      (a, b) => a.kind.localeCompare(b.kind) || a.displayName.localeCompare(b.displayName),
    );
  }

  async frozenSets(): Promise<readonly FrozenSetSummary[]> {
    const page = await firstValueFrom(
      listSnapshots(this.http, this.rootUrl, { workspaceId: this.context.workspaceId, limit: 100 }),
    );
    return page.body.items.map(toFrozenSet);
  }
}

/** The API may send int64 values as strings; the UI works with numbers. */
const num = (value: unknown): number | null =>
  value === null || value === undefined || value === '' ? null : Number(value);

function toFolder(f: SavedSearchFolder): SavedSearchFolder {
  return {
    folderId: f.folderId,
    name: f.name,
    parentFolderId: f.parentFolderId ?? null,
    version: Number(f.version),
  };
}

export function toSummary(s: SavedSearchSummary): SavedSearchSummary {
  return {
    savedSearchId: s.savedSearchId,
    name: s.name,
    folderId: s.folderId ?? null,
    owner: s.owner,
    scope: s.scope,
    sharedWith: s.sharedWith ?? [],
    lastRunAt: s.lastRunAt ?? null,
    lastHitCount: num(s.lastHitCount),
    lastHitRelation: s.lastHitRelation ?? null,
    lastRunFreshness: s.lastRunFreshness ?? null,
    modifiedAt: s.modifiedAt,
    version: Number(s.version),
  };
}

function toSaved(s: SavedSearch): SavedSearch {
  return {
    ...toSummary(s),
    query: s.query ?? '',
    columns: s.columns ?? [],
    sort: s.sort ?? [],
    includeFamily: !!s.includeFamily,
    astVersion: num(s.astVersion),
  };
}

function toFrozenSet(s: SnapshotResource): FrozenSetSummary {
  return {
    snapshotId: s.snapshotId,
    name: s.name,
    purpose: s.purpose,
    status: s.status,
    documentCount: num(s.documentCount),
    frozenAt: (s.materializedAt ?? s.selectedAt ?? s.createdAt ?? null) as string | null,
    createdBy: s.createdBy,
  };
}
