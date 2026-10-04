import type { Route } from '@playwright/test';

/**
 * Saved searches for the e2e mock API (E16-T11), in the shapes of the wave-9 shared contract "Saved searches" (#71):
 * folders, the cursor-paged list of visible searches, get/create/update/delete with If-Match, clone and sharing, and
 * the sharing candidates (`GET …/members`) and frozen sets (`GET …/snapshots`). `POST …/searches` with a
 * `savedSearchId` records the run (see mock-api.ts).
 */

interface Principal {
  kind: 'user' | 'group';
  id: string;
  displayName: string;
}

interface Folder {
  folderId: string;
  name: string;
  parentFolderId: string | null;
  version: number;
}

interface Saved {
  savedSearchId: string;
  name: string;
  folderId: string | null;
  owner: { userId: string; displayName: string };
  scope: 'private' | 'shared';
  sharedWith: Principal[];
  lastRunAt: string | null;
  lastHitCount: number | null;
  lastHitRelation: 'eq' | 'gte' | null;
  lastRunFreshness: { state: 'current' | 'catchingUp'; asOf: string } | null;
  modifiedAt: string;
  version: number;
  query: string;
  columns: string[];
  sort: { field: string; direction: string }[];
  includeFamily: boolean;
  astVersion: number;
}

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };
const COLLEAGUE = { userId: 'user-2', displayName: 'Jamie Lee' };

/** Users and groups of the workspace (`GET …/members` items). */
const MEMBERS = [
  { kind: 'user', userId: 'user-1', displayName: 'Alex Reviewer', groupName: null },
  { kind: 'user', userId: 'user-2', displayName: 'Jamie Lee', groupName: null },
  { kind: 'user', userId: 'user-3', displayName: 'Sam Patel', groupName: null },
  { kind: 'group', userId: null, displayName: null, groupName: 'Review Team' },
].map((m, i) => ({
  assignmentId: `assign-${i + 1}`,
  role: 'Reviewer',
  assignedAt: '2026-10-01T09:00:00Z',
  ...m,
}));

/** The request the mock received for a saved-search write. */
export interface SavedSearchRequest {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  ifMatch: string | null;
}

export class SavedSearchesMock {
  readonly folders: Folder[] = [
    { folderId: 'folder-1', name: 'First pass', parentFolderId: null, version: 1 },
    { folderId: 'folder-2', name: 'Privilege', parentFolderId: null, version: 1 },
    { folderId: 'folder-3', name: 'Hot documents', parentFolderId: 'folder-1', version: 1 },
  ];
  readonly searches: Saved[] = [
    saved('ss-1', 'Responsive emails', 'folder-1', ME, 'filetype:email', {
      lastRunAt: '2026-10-03T10:42:00Z',
      lastHitCount: 120,
      lastHitRelation: 'eq',
      lastRunFreshness: { state: 'current', asOf: '2026-10-03T10:42:00Z' },
    }),
    saved('ss-2', 'Termination clauses', 'folder-3', ME, '"termination" W/10 notice', {
      scope: 'shared',
      sharedWith: [{ kind: 'group', id: 'Review Team', displayName: 'Review Team' }],
      lastRunAt: '2026-10-02T15:10:00Z',
      lastHitCount: 10_000,
      lastHitRelation: 'gte',
      lastRunFreshness: { state: 'catchingUp', asOf: '2026-10-02T15:10:00Z' },
    }),
    saved('ss-3', 'Privilege candidates', 'folder-2', COLLEAGUE, 'privileged OR counsel', {
      scope: 'shared',
      sharedWith: [{ kind: 'user', id: 'user-1', displayName: 'Alex Reviewer' }],
    }),
    saved('ss-4', 'PDF attachments', null, ME, 'extension:pdf'),
  ];
  /** Writes received (create, update, delete, clone, sharing, folders). */
  readonly requests: SavedSearchRequest[] = [];
  /** Saved-search ids run through `POST …/searches` with `savedSearchId`. */
  readonly runs: string[] = [];
  private next = 100;

  /** The stored query of a saved search, or null when there is none (the search answers 404). */
  queryOf(savedSearchId: string): string | null {
    return this.searches.find((x) => x.savedSearchId === savedSearchId)?.query ?? null;
  }

  /** `POST …/searches {savedSearchId}` ran: record the run and its hit count. */
  run(savedSearchId: string, hits: number): void {
    const s = this.searches.find((x) => x.savedSearchId === savedSearchId);
    if (!s) return;
    this.runs.push(savedSearchId);
    const now = new Date().toISOString();
    s.lastRunAt = now;
    s.lastHitCount = hits;
    s.lastHitRelation = 'eq';
    s.lastRunFreshness = { state: 'current', asOf: now };
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const ws = /^\/api\/v1\/workspaces\/[^/]+\/(.+)$/.exec(path)?.[1];
    if (!ws) return undefined;
    const request = route.request();
    const body = () => (request.postDataJSON() ?? {}) as Record<string, unknown>;
    const record = () =>
      this.requests.push({
        method,
        path,
        body: method === 'DELETE' ? null : body(),
        ifMatch: request.headers()['if-match'] ?? null,
      });
    const json = (status: number, data: unknown) => route.fulfill({ status, json: data });
    const notFound = () =>
      route.fulfill({
        status: 404,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Not found', status: 404 }),
      });

    if (ws === 'members' && method === 'GET') {
      return json(200, {
        items: MEMBERS,
        nextCursor: null,
        total: { value: MEMBERS.length, relation: 'eq' },
      });
    }
    if (ws === 'snapshots' && method === 'GET') {
      return json(200, {
        items: [frozenSet('snapshot-9', 42)],
        nextCursor: null,
        total: { value: 1, relation: 'eq' },
      });
    }

    if (ws === 'saved-search-folders') {
      if (method === 'GET') return json(200, { items: this.folders });
      if (method === 'POST') {
        record();
        const b = body();
        const folder: Folder = {
          folderId: `folder-${this.next++}`,
          name: String(b['name']),
          parentFolderId: (b['parentFolderId'] as string | null) ?? null,
          version: 1,
        };
        this.folders.push(folder);
        return json(201, folder);
      }
    }
    const folderMatch = /^saved-search-folders\/([^/]+)$/.exec(ws);
    if (folderMatch) {
      const folder = this.folders.find((f) => f.folderId === folderMatch[1]);
      if (!folder) return notFound();
      record();
      if (method === 'PUT') {
        const b = body();
        folder.name = String(b['name']);
        folder.parentFolderId = (b['parentFolderId'] as string | null) ?? null;
        folder.version++;
        return json(200, folder);
      }
      if (method === 'DELETE') {
        const used =
          this.folders.some((f) => f.parentFolderId === folder.folderId) ||
          this.searches.some((s) => s.folderId === folder.folderId);
        if (used) {
          return route.fulfill({
            status: 409,
            contentType: 'application/problem+json',
            body: JSON.stringify({
              title: 'Folder not empty',
              status: 409,
              code: 'FOLDER_NOT_EMPTY',
            }),
          });
        }
        this.folders.splice(this.folders.indexOf(folder), 1);
        return route.fulfill({ status: 204 });
      }
    }

    if (ws === 'saved-searches') {
      if (method === 'GET') {
        const folderId = url.searchParams.get('folderId');
        const q = url.searchParams.get('q')?.toLowerCase();
        const items = this.searches
          .filter((s) => !folderId || s.folderId === folderId)
          .filter((s) => !q || s.name.toLowerCase().includes(q))
          .map(summary);
        return json(200, { items, nextCursor: null });
      }
      if (method === 'POST') {
        record();
        const b = body();
        const s = saved(
          `ss-${this.next++}`,
          String(b['name']),
          (b['folderId'] as string | null) ?? null,
          ME,
          String(b['query'] ?? ''),
        );
        this.searches.push(s);
        return json(201, s);
      }
    }
    const item = /^saved-searches\/([^/]+)(?:\/(clone|sharing))?$/.exec(ws);
    if (item) {
      const s = this.searches.find((x) => x.savedSearchId === item[1]);
      if (!s) return notFound();
      const action = item[2];
      if (!action && method === 'GET') return json(200, s);
      record();
      if (!action && method === 'PUT') {
        const b = body();
        s.name = String(b['name']);
        s.folderId = (b['folderId'] as string | null) ?? null;
        s.query = String(b['query'] ?? s.query);
        s.version++;
        s.modifiedAt = new Date().toISOString();
        return json(200, s);
      }
      if (!action && method === 'DELETE') {
        this.searches.splice(this.searches.indexOf(s), 1);
        return route.fulfill({ status: 204 });
      }
      if (action === 'clone' && method === 'POST') {
        const b = body();
        const copy = saved(
          `ss-${this.next++}`,
          String(b['name'] ?? `Copy of ${s.name}`),
          (b['folderId'] as string | null) ?? null,
          ME,
          s.query,
        );
        this.searches.push(copy);
        return json(201, copy);
      }
      if (action === 'sharing' && method === 'PUT') {
        const wanted = (body()['sharedWith'] as { kind: string; id: string }[]) ?? [];
        s.sharedWith = wanted.map((w) => {
          const m = MEMBERS.find((x) =>
            w.kind === 'group' ? x.groupName === w.id : x.userId === w.id,
          );
          return {
            kind: w.kind as 'user' | 'group',
            id: w.id,
            displayName: m?.displayName ?? m?.groupName ?? w.id,
          };
        });
        s.scope = s.sharedWith.length ? 'shared' : 'private';
        s.version++;
        return json(200, s);
      }
    }
    return undefined;
  }
}

function saved(
  id: string,
  name: string,
  folderId: string | null,
  owner: { userId: string; displayName: string },
  query: string,
  extra: Partial<Saved> = {},
): Saved {
  return {
    savedSearchId: id,
    name,
    folderId,
    owner,
    scope: 'private',
    sharedWith: [],
    lastRunAt: null,
    lastHitCount: null,
    lastHitRelation: null,
    lastRunFreshness: null,
    modifiedAt: '2026-10-01T09:00:00Z',
    version: 1,
    query,
    columns: [],
    sort: [],
    includeFamily: false,
    astVersion: 1,
    ...extra,
  };
}

function summary(s: Saved) {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { query, columns, sort, includeFamily, astVersion, ...rest } = s;
  return rest;
}

/** A frozen set of the Searches section's "Frozen sets" node (`SnapshotResource`). */
function frozenSet(id: string, count: number) {
  return {
    snapshotId: id,
    name: 'Mass Edit 2026-10-03',
    purpose: 'bulkCoding',
    status: 'ready',
    statusReason: null,
    source: {
      kind: 'query',
      query: null,
      normalizedQuery: null,
      snapshotId: null,
      requestedCount: null,
    },
    documentCount: count,
    inclusionCounts: {},
    searchGeneration: 18432,
    projectionGeneration: 18432,
    selectedWhileIndexing: false,
    selectedAt: '2026-10-03T10:42:00Z',
    materializationStrategy: 'Inline',
    pageSize: 1000,
    pageCount: 1,
    rootSha256: null,
    createdBy: 'user-1',
    createdAt: '2026-10-03T10:42:00Z',
    materializedAt: '2026-10-03T10:42:01Z',
    expiresAt: null,
    expiredAt: null,
  };
}
