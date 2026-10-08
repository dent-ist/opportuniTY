import type { Page, Route } from '@playwright/test';
import { CODING_FIELDS, CodingMock } from './mock-coding';
import { documentText, serveContent, snippetsFor, type Rendition } from './mock-content';
import { FreshnessMock, type MockFreshnessState } from './mock-freshness';
import { ImportsMock } from './mock-imports';
import { JobsMock } from './mock-jobs';
import { SavedSearchesMock } from './mock-saved-searches';
import { SearchTermReportsMock } from './mock-search-term-reports';
import { GridViewsMock } from './mock-grid-views';
import { HighlightsMock } from './mock-highlights';
import { RedactionsMock } from './mock-redactions';
import { FieldAdminMock } from './mock-field-admin';
import { RelationshipsMock, hitRelations } from './mock-relationships';

/**
 * In-browser stand-in for the BFF and API, mirroring src/app/core/api/fake-api.testing.ts: answers the routes the
 * shell needs (`/api/v1/me`, the user's preferences, the workspace directory), the query bar's field catalogue, query
 * validation and query history, and 404 problem details for anything else, so a page that starts calling a new
 * endpoint fails visibly instead of hanging.
 */
export interface MockApiOptions {
  signedIn?: boolean;
  /** Effective permissions in every workspace; defaults to all (so admin routes are reachable). */
  permissions?: readonly string[];
  /** The signed-in user's stored preferences (`/api/v1/me/preferences`); starts empty. */
  preferences?: Record<string, unknown>;
  /** Documents every search of the mock finds (the document list); default 250. */
  documents?: number;
  /** Delay of the document content gateway (`GET …/documents/{id}/…`), to make prefetch measurable. */
  contentDelayMs?: number;
  /** How long a coding save stays "indexing" before it is searchable (default 600 ms). */
  indexDelayMs?: number;
  /** Delay of `PUT …/coding`, to measure the coding acknowledgement. */
  codingSaveDelayMs?: number;
  /** Frozen sets answer 202 and become Ready on the first `GET …/snapshots/{id}` (a large set). */
  snapshotsMaterialize?: boolean;
  /** Frozen sets report documents selected while still indexing (ADR-002 §4). */
  selectedWhileIndexing?: boolean;
  /** Document number whose extracted text is 10 MB (E16-T04 performance). */
  largeTextDocument?: number;
  /** The import pre-flight reports a blocking error (E08-T08). */
  preflightBlocking?: boolean;
  /** Serve the live job stream (`GET …/job-events`); false answers 404, so pages poll (E06-T07). Default true. */
  jobEvents?: boolean;
  /** Installation permissions of the signed-in user (`/api/v1/me`); default: may create workspaces. */
  installationPermissions?: readonly string[];
  /** Whether the session satisfies MFA (`/api/v1/me`); default true. Without it creating a workspace needs a step-up. */
  mfa?: boolean;
  /** Search index state at the start (wave-10 freshness, E16-T07); default current. */
  freshness?: MockFreshnessState;
  /** Propagation targets above this run as a job (wave-12 contract; the API default is 1,000). */
  propagationThreshold?: number;
}

/** A request the mock answered, with its JSON body and Idempotency-Key. */
export interface MockRequest {
  body: Record<string, unknown>;
  idempotencyKey: string | null;
}

/** One protected-content gateway audit record of the mock, as ADR-013 defines them. */
export interface MockAuditEvent {
  action: 'Retrieved' | 'Viewed';
  documentId: string;
  /** What was delivered (content API, E11-T01); absent for the first-chunk `GET …/text`. */
  rendition?: Rendition;
  purpose?: 'display' | 'prefetch' | 'download';
  retrievalId?: string | null;
}

/** Test-side handle on the mock's state ("server" side of the scenario). */
export interface MockControl {
  /** Requests to endpoints the mock had no answer for. */
  readonly unhandled: string[];
  /** Field and coding layout administration (E04-T06): the workspace's fields and layouts. */
  readonly fieldAdmin: FieldAdminMock;
  /** Gateway audit log: `Retrieved` per content delivery (with its purpose), `Viewed` per view beacon. */
  readonly audit: MockAuditEvent[];
  /** Running searches expire: page requests answer 404 until the next search runs (Q-33). */
  expireSearches(): void;
  /** Documents (1-based numbers) that no longer match any search, e.g. after a recode. */
  removeDocuments(...numbers: number[]): void;
  /** The coding store: saves received, and another user's changes (`coding.codeAsOtherUser`). */
  readonly coding: CodingMock;
  /** `POST …/snapshots` requests (frozen sets). */
  readonly snapshots: MockRequest[];
  /** `POST …/bulk-coding` requests (Mass Edit jobs, the shape assumed for E10-T04). */
  readonly bulkCoding: MockRequest[];
  /** Imports (E08-T08): previews, pre-flights and started imports received. */
  readonly imports: ImportsMock;
  /** Job operations (E06-T07): the jobs, server-side changes (`jobs.update`), cancels and retries received. */
  readonly jobs: JobsMock;
  /** Workspace writes (E04-T07): `POST /api/v1/workspaces` and `PUT /api/v1/workspaces/{id}` bodies with If-Match. */
  readonly workspaceWrites: {
    method: string;
    body: Record<string, unknown>;
    ifMatch: string | null;
  }[];
  /** Saved searches (E16-T11): folders, searches, writes received and runs by id. */
  readonly savedSearches: SavedSearchesMock;
  /** Highlight Sets, toggles and term hits (E16-T12). */
  readonly highlights: HighlightsMock;
  /** Redaction Sets, reasons and document redactions (E11-T04); `concurrentEdit(n)` plays another user's save. */
  readonly redactions: RedactionsMock;
  /** Search freshness (E16-T07): move the index between current, updating and delayed. */
  readonly freshness: FreshnessMock;
  /** Search Terms Reports (#180): reports, writes received (create with Idempotency-Key, re-run, delete, export). */
  readonly termReports: SearchTermReportsMock;
  /** Document-list views and the saved layout (E16-T09). */
  readonly gridViews: GridViewsMock;
  /** The body of the last `POST …/searches` (sort, fields). */
  readonly lastSearch: () => Record<string, unknown> | null;
  /** Relationships and coding propagation (E16-T10): previews and applies received. */
  readonly relationships: RelationshipsMock;
}

/**
 * A search result page of the document list (`SearchResultPage`) over document numbers `docs` (in order);
 * cursors encode the page number (`p<n>`).
 */
function searchPage(
  docs: readonly number[],
  pageSize: number,
  number: number,
  query = '',
  freshness: Record<string, unknown> = {
    asOf: '2026-10-03T10:42:00Z',
    current: true,
    servedGeneration: null,
  },
  fields: readonly string[] = [],
) {
  const total = docs.length;
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const exact = total <= 10_000;
  const first = (number - 1) * pageSize;
  // Families of a parent and one attachment (n % 4 === 3 → n + 1), the rest standalone.
  const items = docs.slice(first, first + pageSize).map((n) => {
    const attachment = n % 4 === 0;
    return {
      documentId: `doc-${n}`,
      controlNumber: `ACM${String(n).padStart(7, '0')}`,
      documentDate: new Date(Date.UTC(2024, 0, 1) + n * 3_600_000).toISOString(),
      familyId: `doc-${attachment ? n - 1 : n}`,
      familySequence: attachment ? 1 : 0,
      parentDocumentId: attachment ? `doc-${n - 1}` : null,
      fileExtension: attachment ? 'pdf' : 'msg',
      fileName: attachment ? `Attachment ${n}.pdf` : `RE: Quarterly terms ${n}.msg`,
      fileSize: 1024 * ((n * 37) % 900) + 512,
      fileType: attachment ? 'PDF' : 'Email',
      isFamilyParent: n % 4 === 3 && n < total,
      mimeType: attachment ? 'application/pdf' : 'application/vnd.ms-outlook',
      pageCount: (n % 7) + 1,
      ...hitRelations(n, total),
      snippets: snippetsFor(n, query),
      // The values of the columns the search asked for (E16-T09): a choice ID for choice fields.
      ...(fields.length > 0
        ? { fields: Object.fromEntries(fields.map((f) => [f, fieldValues(f, n, total)])) }
        : {}),
    };
  });
  return {
    searchId: 'search-1',
    normalized: '',
    items,
    page: {
      number,
      size: pageSize,
      pageCount: exact ? pageCount : null,
      isFirst: number === 1,
      isLast: number >= pageCount,
    },
    total: { value: exact ? total : 10_000, relation: exact ? 'eq' : 'gte' },
    freshness,
    nextCursor: number < pageCount ? `p${number + 1}` : null,
    previousCursor: number > 1 ? `p${number - 1}` : null,
    resultsRefreshed: false,
  };
}

/** A deterministic value of field `queryName` for document `n` (choice fields: one of their choice IDs). */
function fieldValues(queryName: string, n: number, total: number): string[] {
  if (queryName === 'email_thread_group') {
    const thread = hitRelations(n, total).emailThreadId;
    return thread ? [thread] : [];
  }
  const field = CODING_FIELDS.find((f) => f.queryName === queryName);
  const choices = field?.choices;
  return [choices ? String(choices[n % choices.length].choiceId) : `${queryName} ${n}`];
}

/** The structural columns of the review grid, with their types (GET …/fields, ADR-007 §3). */
const SYSTEM_FIELDS = [
  ['controlnumber', 'Control Number', 'keyword'],
  ['date', 'Document Date', 'date'],
  ['filename', 'File Name', 'text'],
  ['filetype', 'File Type', 'keyword'],
  ['extension', 'File Extension', 'keyword'],
  ['filesize', 'File Size', 'integer'],
  ['pagecount', 'Page Count', 'integer'],
  // Email Thread ID (E16-T10): a relationship column, values from the hit's thread.
  ['email_thread_group', 'Email Thread Group', 'keyword'],
].map(([queryName, displayName, type], i) => ({
  fieldId: i + 1,
  queryName,
  displayName,
  type,
  storage: 'column',
  multiValue: false,
  isSystem: true,
  isHidden: false,
  isSecurityAffecting: false,
  datePrecision: null,
  reducedCapabilities: false,
  capabilities: {
    sortable: true,
    filterable: true,
    aggregatable: false,
    fullText: false,
    highlightable: false,
    wildcard: type === 'keyword' || type === 'text',
    leadingWildcard: queryName === 'filename',
    rangeable: type !== 'keyword' && type !== 'text',
    exists: true,
  },
  choices: null,
}));

export const ALL_PERMISSIONS = [
  // The #47 permission catalogue (docs/security/permission-matrix.md).
  'Document.View',
  'Document.DownloadNative',
  'Document.Print',
  'Document.ViewQuarantined',
  'Search.Execute',
  'SavedSearch.Share',
  'SearchTermReport.Run',
  'Coding.Write',
  'Coding.WritePrivilege',
  'Coding.Bulk',
  'Redaction.Apply',
  'Redaction.Remove',
  'Import.Run',
  'Import.Overlay',
  'Export.Create',
  'Export.Download',
  'Production.Create',
  'Production.Finalize',
  'PrivilegeLog.Generate',
  'Job.ViewAll',
  'Job.Manage',
  'Job.Replay',
  'View.ManageShared',
  'Audit.Read',
  'Audit.ReadSearchText',
  'Workspace.ManageUsers',
  'Workspace.ManageSecurity',
  'Workspace.ManageFields',
  'Workspace.RequestDeletion',
  'HighlightSet.Manage',
] as const;

const WORKSPACE_DEFAULTS = {
  displayTimeZone: 'UTC',
  status: 'active',
  createdAt: '2026-10-03T00:00:00.000Z',
} as const;

/** `WorkspaceSummary` items of `GET /api/v1/workspaces`. */
export const WORKSPACES = [
  { workspaceId: 'ws-1', name: 'Acme v. Widget', matterNumber: 'M-1001', ...WORKSPACE_DEFAULTS },
  { workspaceId: 'ws-2', name: 'Beta Holdings', matterNumber: 'M-1002', ...WORKSPACE_DEFAULTS },
] as const;

const principal = {
  userId: 'user-1',
  displayName: 'Alex Reviewer',
  email: 'alex@example.test',
  groups: [],
  mfa: true,
  sessionExpiresAt: null,
  installationPermissions: ['Installation.ManageWorkspaces'],
};

/** Read-only search placement of the mock's workspaces (`WorkspaceResource.searchPlacement`). */
const SEARCH_PLACEMENT = { kind: 'shared', projectionGeneration: 18432, state: 'active' } as const;

/** `WorkspaceMemberResource` of a user's role assignment. */
function member(assignmentId: string, displayName: string, role: string) {
  return {
    assignmentId,
    kind: 'user',
    role,
    userId: `user-${assignmentId}`,
    displayName,
    groupName: null,
    assignedAt: '2026-10-03T00:00:00.000Z',
  };
}

/** `GET /api/v1/workspaces/{id}/fields` items: the grid's structural fields and the coding fields (./mock-coding.ts). */
export const FIELDS = [...SYSTEM_FIELDS, ...CODING_FIELDS];

/** `SnapshotResource` of a frozen set of `count` documents (#90). */
function snapshot(id: string, count: number, ready: boolean, whileIndexing: boolean) {
  return {
    snapshotId: id,
    name: 'Mass Edit',
    purpose: 'bulkCoding',
    status: ready ? 'ready' : 'materializing',
    statusReason: null,
    source: {
      kind: 'query',
      query: null,
      normalizedQuery: null,
      snapshotId: null,
      requestedCount: null,
    },
    documentCount: ready ? count : null,
    inclusionCounts: {},
    searchGeneration: ready ? 18432 : null,
    projectionGeneration: ready ? 18432 : null,
    selectedWhileIndexing: ready ? whileIndexing : null,
    selectedAt: '2026-10-03T10:42:00Z',
    materializationStrategy: 'Inline',
    pageSize: 1000,
    pageCount: ready ? Math.ceil(count / 1000) : null,
    rootSha256: null,
    createdBy: 'user-1',
    createdAt: '2026-10-03T10:42:00Z',
    materializedAt: ready ? '2026-10-03T10:42:01Z' : null,
    expiresAt: null,
    expiredAt: null,
  };
}

/**
 * `JobResource` of a Mass Edit over `count` documents after `polls` progress reads: half saved, then finished with
 * two documents skipped (Q-07), then searchable.
 */
function bulkJob(id: string, snapshotId: string, count: number, polls: number) {
  const saved = polls === 0 ? Math.floor(count / 2) : count;
  const skipped = polls === 0 ? 0 : Math.min(2, count);
  return {
    jobId: id,
    workspaceId: 'ws-1',
    jobType: 'bulkCoding',
    status: polls === 0 ? 'running' : 'completed',
    statusReason: null,
    initiatedBy: 'user-1',
    targetSnapshotId: snapshotId,
    createdAt: '2026-10-03T10:43:00Z',
    startedAt: '2026-10-03T10:43:00Z',
    finishedAt: polls === 0 ? null : '2026-10-03T10:43:05Z',
    committed: {
      chunksTotal: 1,
      chunksCommitted: polls === 0 ? 0 : 1,
      chunksPending: polls === 0 ? 1 : 0,
      chunksFailed: 0,
      chunksCancelled: 0,
      itemsApplied: saved - skipped,
      itemsUnchanged: 0,
      itemsSkippedConcurrentEdit: skipped,
      itemsFailed: 0,
      itemsExcludedNoAccess: 0,
    },
    indexed: {
      indexTasksTotal: 1,
      indexTasksApplied: polls >= 2 ? 1 : 0,
      state: polls >= 2 ? 'current' : 'indexing',
    },
  };
}

function problem(status: number, title: string) {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title, status }),
  };
}

/** Installs the mock; returns its control handle (unanswered requests, audit log, scenario switches). */
export async function mockApi(page: Page, options: MockApiOptions = {}): Promise<MockControl> {
  const {
    signedIn = true,
    permissions = ALL_PERMISSIONS,
    documents = 250,
    contentDelayMs = 0,
  } = options;
  let pageSize = 100;
  let total = documents;
  const removed = new Set<number>();
  let expired = false;
  let lastQuery = '';
  let retrievals = 0;
  const audit: MockAuditEvent[] = [];
  const matching = () =>
    Array.from({ length: total }, (_, i) => i + 1).filter((n) => !removed.has(n));
  // The "server" copy of the preferences outlives reloads of the page, like the real profile.
  const preferences: Record<string, unknown> = { ...options.preferences };
  // Likewise the user's query history per workspace (newest first, distinct, at most 50).
  const history = new Map<string, { query: string; ranAt: string }[]>();
  const json = (route: Route, body: unknown) => route.fulfill({ json: body });
  const unhandled: string[] = [];
  const coding = new CodingMock({
    write: permissions.includes('Coding.Write'),
    writePrivilege: permissions.includes('Coding.WritePrivilege'),
    indexDelayMs: options.indexDelayMs ?? 600,
    saveDelayMs: options.codingSaveDelayMs ?? 0,
  });
  const snapshots: MockRequest[] = [];
  const bulkCoding: MockRequest[] = [];
  const frozen = new Map<string, number>();
  const jobs = new Map<string, { snapshotId: string; polls: number }>();
  const imports = new ImportsMock({ preflightBlocking: options.preflightBlocking });
  // Workspaces (E04-T07): the seeded ones plus any the test creates; a created one starts empty (no imports, only
  // system fields, an empty Default layout, the creator's own role).
  const workspaces = new Map<string, Record<string, unknown>>(
    WORKSPACES.map((w) => [
      w.workspaceId,
      {
        ...w,
        breakGlassActive: false,
        storageProfile: 'default',
        version: 1,
        updatedAt: w.createdAt,
        searchPlacement: SEARCH_PLACEMENT,
      },
    ]),
  );
  const created = new Set<string>();
  const workspaceWrites: MockControl['workspaceWrites'] = [];
  const termReports = new SearchTermReportsMock();
  const jobsMock = new JobsMock({
    userId: principal.userId,
    viewAll: permissions.includes('Job.ViewAll'),
    stream: options.jobEvents ?? true,
    lookup: (id) => imports.job(id) ?? termReports.job(id),
  });
  const savedSearches = new SavedSearchesMock();
  const gridViews = new GridViewsMock(permissions.includes('View.ManageShared'));
  let lastSearch: Record<string, unknown> | null = null;
  let lastFields: string[] = [];
  const highlights = new HighlightsMock(() => lastQuery);
  const redactions = new RedactionsMock();
  const fieldAdmin = new FieldAdminMock(FIELDS);
  const freshness = new FreshnessMock(options.freshness);
  const relationships = new RelationshipsMock(
    coding,
    () => documents,
    options.propagationThreshold,
  );
  const control: MockControl = {
    relationships,
    freshness,
    imports,
    jobs: jobsMock,
    workspaceWrites,
    savedSearches,
    termReports,
    gridViews,
    lastSearch: () => lastSearch,
    highlights,
    redactions,
    fieldAdmin,
    unhandled,
    audit,
    coding,
    expireSearches: () => (expired = true),
    removeDocuments: (...numbers) => numbers.forEach((n) => removed.add(n)),
    snapshots,
    bulkCoding,
  };

  await page.route(/\/(api|bff)\//, (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/v1/me') {
      return signedIn
        ? json(route, {
            ...principal,
            mfa: options.mfa ?? principal.mfa,
            installationPermissions:
              options.installationPermissions ?? principal.installationPermissions,
          })
        : route.fulfill(problem(401, 'Unauthorized'));
    }
    const method = route.request().method();
    // A workspace created in the test is empty: answered before the shared import/field/layout mocks.
    const fresh = /^\/api\/v1\/workspaces\/([^/]+)\/(imports|fields|coding-layouts|members)$/.exec(
      path,
    );
    if (signedIn && method === 'GET' && fresh && created.has(fresh[1])) {
      const items =
        fresh[2] === 'fields'
          ? SYSTEM_FIELDS
          : fresh[2] === 'coding-layouts'
            ? [{ layoutId: 'layout-default', name: 'Default', isDefault: true, sections: [] }]
            : fresh[2] === 'members'
              ? [member('a-1', 'Alex Reviewer', 'workspaceAdmin')]
              : [];
      return json(route, {
        items,
        nextCursor: null,
        total: { value: items.length, relation: 'eq' },
      });
    }
    // Fields, choices and coding layouts (E04-T06): ./mock-field-admin.ts.
    const administered = signedIn ? fieldAdmin.handle(route, method, path) : undefined;
    if (administered) return administered;
    if (signedIn && method === 'GET' && /^\/api\/v1\/workspaces\/[^/]+\/members$/.test(path)) {
      const items = [
        member('a-1', 'Alex Reviewer', 'workspaceAdmin'),
        member('a-2', 'Sam Senior', 'seniorReviewer'),
        member('a-3', 'Riley Reviewer', 'reviewer'),
      ];
      return json(route, { items, nextCursor: null, total: { value: 3, relation: 'eq' } });
    }
    if (signedIn && path === '/api/v1/me/preferences' && method === 'GET') {
      return json(route, { values: preferences });
    }
    const preference = /^\/api\/v1\/me\/preferences\/([A-Za-z][\w.-]*)$/.exec(path);
    if (signedIn && preference && (method === 'PUT' || method === 'DELETE')) {
      if (method === 'PUT') preferences[preference[1]] = route.request().postDataJSON();
      else delete preferences[preference[1]];
      return route.fulfill({ status: 204 });
    }
    if (
      signedIn &&
      method === 'POST' &&
      /^\/api\/v1\/workspaces\/[^/]+\/query-validations$/.test(path)
    ) {
      const query = String((route.request().postDataJSON() as { query?: string })?.query ?? '');
      return json(route, {
        valid: true,
        astVersion: 1,
        normalized: query.trim(),
        errors: [],
        warnings: [],
      });
    }
    if (signedIn && method === 'GET' && /^\/api\/v1\/workspaces\/[^/]+\/fields$/.test(path)) {
      return json(route, {
        items: FIELDS,
        nextCursor: null,
        total: { value: FIELDS.length, relation: 'eq' },
      });
    }
    const queryHistory = /^\/api\/v1\/workspaces\/([^/]+)\/query-history$/.exec(path);
    if (signedIn && queryHistory) {
      const entries = history.get(queryHistory[1]) ?? [];
      if (method === 'GET') return json(route, { items: entries });
      if (method === 'POST') {
        const query = String((route.request().postDataJSON() as { query?: string }).query).trim();
        history.set(
          queryHistory[1],
          [
            { query, ranAt: new Date().toISOString() },
            ...entries.filter((e) => e.query !== query),
          ].slice(0, 50),
        );
        return route.fulfill({ status: 204 });
      }
    }
    if (signedIn && /^\/api\/v1\/workspaces\/[^/]+\/searches$/.test(path) && method === 'POST') {
      const body = route.request().postDataJSON() as {
        pageSize?: number;
        query?: string;
        savedSearchId?: string;
        searchTermReportId?: string;
        termId?: string;
        highlight?: boolean | null;
        fields?: string[];
      };
      lastSearch = { ...body };
      lastFields = body?.fields ?? [];
      pageSize = Number(body?.pageSize ?? 100);
      if (body?.searchTermReportId) {
        // One term's hits within the report's frozen set (wave-11 contract): unknown → 404.
        const term = termReports.term(body.searchTermReportId, String(body.termId ?? ''));
        if (!term || term.documentsWithHits === null)
          return route.fulfill(problem(404, 'Not found'));
        total = Math.min(documents, term.documentsWithHits);
        expired = false;
        lastQuery = body?.highlight === false ? '' : term.expression;
        return json(route, {
          ...searchPage(matching(), pageSize, 1, lastQuery, freshness.served()),
          searchTermReportId: body.searchTermReportId,
          termId: body.termId,
        });
      }
      if (body?.savedSearchId) {
        // A saved search runs its stored query (wave-9 contract): unknown or not visible → 404.
        const stored = savedSearches.queryOf(body.savedSearchId);
        if (stored === null) return route.fulfill(problem(404, 'Not found'));
        body.query = stored;
      }
      // A fielded query (the filter row) narrows the list, so filtering is visible end to end.
      total = body?.query?.includes(':') ? Math.min(documents, 12) : documents;
      expired = false;
      // Like the API, snippets come only with highlighting (on by default); the search keeps the setting for its pages.
      lastQuery = body?.highlight === false ? '' : String(body?.query ?? '');
      if (!body?.savedSearchId)
        return json(
          route,
          searchPage(matching(), pageSize, 1, lastQuery, freshness.served(), lastFields),
        );
      savedSearches.run(body.savedSearchId, matching().length);
      return json(route, {
        ...searchPage(matching(), pageSize, 1, lastQuery, freshness.served(), lastFields),
        savedSearchId: body.savedSearchId,
      });
    }
    if (signedIn && /^\/api\/v1\/workspaces\/[^/]+\/searches\/[^/]+\/pages$/.test(path)) {
      if (expired) return route.fulfill(problem(404, 'Not found'));
      const docs = matching();
      const cursor = url.searchParams.get('cursor');
      const last = url.searchParams.get('last') === 'true';
      const n = cursor
        ? Number(cursor.slice(1))
        : last
          ? Math.max(1, Math.ceil(docs.length / pageSize))
          : Number(url.searchParams.get('page') ?? 1);
      return json(route, searchPage(docs, pageSize, n, lastQuery, freshness.served(), lastFields));
    }
    // Document-list views and the layout (E16-T09): ./mock-grid-views.ts.
    const viewRoute = signedIn ? gridViews.handle(route, method, path) : undefined;
    if (viewRoute) return viewRoute;
    // Search freshness (E16-T07): ./mock-freshness.ts.
    const freshnessRoute = signedIn ? freshness.handle(route, method, path) : undefined;
    if (freshnessRoute) return freshnessRoute;
    // Saved searches (E16-T11): ./mock-saved-searches.ts.
    const savedSearch = signedIn ? savedSearches.handle(route, method, path, url) : undefined;
    if (savedSearch) return savedSearch;
    // Search Terms Reports (#180): ./mock-search-term-reports.ts.
    const termReport = signedIn ? termReports.handle(route, method, path, url) : undefined;
    if (termReport) return termReport;
    // Highlight Sets and term hits (E16-T12): ./mock-highlights.ts.
    const highlighted = signedIn ? highlights.handle(route, method, path, url) : undefined;
    if (highlighted) return highlighted;
    // Redactions (E11-T04): ./mock-redactions.ts.
    const redacted = signedIn ? redactions.handle(route, method, path) : undefined;
    if (redacted) return redacted;
    // Imports (E08-T08): ./mock-imports.ts.
    const imported = signedIn ? imports.handle(route, method, path) : undefined;
    if (imported) return imported;
    // Coding and coding layouts (E10-T01, E04-T03): ./mock-coding.ts.
    const coded = signedIn ? coding.handle(route, method, path) : undefined;
    if (coded) return coded;
    // Relationships and coding propagation (E16-T10): ./mock-relationships.ts.
    const related = signedIn ? relationships.handle(route, method, path, url) : undefined;
    if (related) return related;
    // Document content API (E16-T04 viewer): metadata, text chunks, pages, page images, natives.
    const served =
      signedIn &&
      serveContent(
        route,
        path,
        url,
        { contentDelayMs, largeTextDocument: options.largeTextDocument },
        (d) => {
          const retrievalId = `retrieval-${++retrievals}`;
          audit.push({ action: 'Retrieved', ...d, retrievalId });
          return retrievalId;
        },
      );
    if (served) return served;
    // Protected-content gateway (E05-T04 / E11-T01): the first chunk of extracted text, audited per delivery
    // with its purpose; only the view beacon records `Viewed` (ADR-013).
    const content =
      /^\/api\/v1\/workspaces\/[^/]+\/documents\/doc-(\d+)\/(text|views|coding)$/.exec(path);
    if (signedIn && content) {
      const documentId = `doc-${content[1]}`;
      if (content[2] === 'text' && method === 'GET') {
        const purpose = url.searchParams.get('purpose') === 'prefetch' ? 'prefetch' : 'display';
        const retrievalId = `retrieval-${++retrievals}`;
        audit.push({ action: 'Retrieved', documentId, purpose, retrievalId });
        const text = documentText(Number(content[1]));
        const respond = () =>
          route.fulfill({
            status: 206,
            contentType: 'text/plain; charset=utf-8',
            headers: {
              'Content-Range': `bytes 0-${text.length - 1}/${text.length}`,
              'X-Opportunity-Retrieval-Id': retrievalId,
              'Cache-Control': 'no-store',
            },
            body: text,
          });
        return contentDelayMs > 0
          ? new Promise<void>((r) => setTimeout(r, contentDelayMs)).then(respond)
          : respond();
      }
      if (content[2] === 'views' && method === 'POST') {
        const body = route.request().postDataJSON() as { retrievalId?: string | null };
        audit.push({ action: 'Viewed', documentId, retrievalId: body?.retrievalId ?? null });
        return route.fulfill({ status: 204 });
      }
    }
    // Frozen sets (#90) and Mass Edit jobs (E10-T04, assumed contract) with progress through the jobs API.
    if (signedIn && method === 'POST' && /^\/api\/v1\/workspaces\/[^/]+\/snapshots$/.test(path)) {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      snapshots.push({
        body,
        idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
      });
      const ids = body['documentIds'] as string[] | undefined;
      const count = ids ? ids.length : matching().length;
      const id = `snapshot-${snapshots.length}`;
      frozen.set(id, count);
      const ready = !options.snapshotsMaterialize;
      return route.fulfill({
        status: ready ? 201 : 202,
        json: snapshot(id, count, ready, !!options.selectedWhileIndexing),
      });
    }
    const snapshotRead = /^\/api\/v1\/workspaces\/[^/]+\/snapshots\/([^/]+)$/.exec(path);
    if (signedIn && method === 'GET' && snapshotRead && frozen.has(snapshotRead[1])) {
      const id = snapshotRead[1];
      return json(route, snapshot(id, frozen.get(id)!, true, !!options.selectedWhileIndexing));
    }
    if (signedIn && method === 'POST' && /^\/api\/v1\/workspaces\/[^/]+\/bulk-coding$/.test(path)) {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      bulkCoding.push({
        body,
        idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
      });
      const id = `job-${bulkCoding.length}`;
      const snapshotId = String(body['snapshotId']);
      jobs.set(id, { snapshotId, polls: 0 });
      return route.fulfill({
        status: 202,
        json: bulkJob(id, snapshotId, frozen.get(snapshotId) ?? 0, 0),
      });
    }
    const jobRead = /^\/api\/v1\/workspaces\/[^/]+\/jobs\/([^/]+)$/.exec(path);
    if (signedIn && method === 'GET' && jobRead && jobs.has(jobRead[1])) {
      const job = jobs.get(jobRead[1])!;
      const body = bulkJob(jobRead[1], job.snapshotId, frozen.get(job.snapshotId) ?? 0, job.polls);
      job.polls++;
      return json(route, body);
    }
    // Job operations and the live job stream (E06-T07): ./mock-jobs.ts.
    const jobRoute = signedIn ? jobsMock.handle(route, method, path, url) : undefined;
    if (jobRoute) return jobRoute;
    if (signedIn && path === '/api/v1/workspaces' && method === 'GET') {
      const items = [...workspaces.values()].map(
        ({ workspaceId, name, matterNumber, displayTimeZone, status, createdAt }) => ({
          workspaceId,
          name,
          matterNumber,
          displayTimeZone,
          status,
          createdAt,
        }),
      );
      return json(route, {
        items,
        nextCursor: null,
        total: { value: items.length, relation: 'eq' },
      });
    }
    if (signedIn && path === '/api/v1/workspaces' && method === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      workspaceWrites.push({ method, body, ifMatch: null });
      if (options.mfa === false)
        return route.fulfill({
          status: 403,
          contentType: 'application/problem+json',
          body: JSON.stringify({
            status: 403,
            title: 'Forbidden',
            code: 'step-up-required',
            stepUpUrl: '/bff/login?stepUp=true',
          }),
        });
      const id = `ws-new-${created.size + 1}`;
      created.add(id);
      const workspace = {
        workspaceId: id,
        name: String(body['name']),
        matterNumber: (body['matterNumber'] as string | null) ?? null,
        displayTimeZone: String(body['displayTimeZone']),
        status: 'active',
        createdAt: '2026-10-04T09:00:00.000Z',
        updatedAt: '2026-10-04T09:00:00.000Z',
        breakGlassActive: false,
        storageProfile: 'default',
        version: 1,
        searchPlacement: null,
      };
      workspaces.set(id, workspace);
      return route.fulfill({ status: 201, json: { ...workspace, permissions: ALL_PERMISSIONS } });
    }
    const wsPath = /^\/api\/v1\/workspaces\/([^/]+)$/.exec(path);
    const ws = wsPath ? workspaces.get(wsPath[1]) : undefined;
    if (signedIn && ws && method === 'PUT') {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      const ifMatch = route.request().headers()['if-match'] ?? null;
      workspaceWrites.push({ method, body, ifMatch });
      if (ifMatch !== `"${ws['version']}"`) return route.fulfill(problem(412, 'Version conflict'));
      const updated = {
        ...ws,
        ...body,
        version: Number(ws['version']) + 1,
        updatedAt: '2026-10-04T10:00:00.000Z',
      };
      workspaces.set(wsPath![1], updated);
      return json(route, { ...updated, permissions });
    }
    if (signedIn && ws && method === 'GET')
      return json(route, {
        ...ws,
        permissions: created.has(wsPath![1]) ? ALL_PERMISSIONS : permissions,
      });
    unhandled.push(`${route.request().method()} ${path}`);
    return route.fulfill(problem(404, 'Not found'));
  });
  return control;
}
