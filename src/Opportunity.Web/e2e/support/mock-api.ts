import type { Page, Route } from '@playwright/test';
import { documentText, serveContent, snippetsFor, type Rendition } from './mock-content';

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
  /** Document number whose extracted text is 10 MB (E16-T04 performance). */
  largeTextDocument?: number;
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
  /** Gateway audit log: `Retrieved` per content delivery (with its purpose), `Viewed` per view beacon. */
  readonly audit: MockAuditEvent[];
  /** Running searches expire: page requests answer 404 until the next search runs (Q-33). */
  expireSearches(): void;
  /** Documents (1-based numbers) that no longer match any search, e.g. after a recode. */
  removeDocuments(...numbers: number[]): void;
}

/**
 * A search result page of the document list (`SearchResultPage`) over document numbers `docs` (in order);
 * cursors encode the page number (`p<n>`).
 */
function searchPage(docs: readonly number[], pageSize: number, number: number, query = '') {
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
      snippets: snippetsFor(n, query),
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
    freshness: { asOf: '2026-10-03T10:42:00Z', current: true, servedGeneration: null },
    nextCursor: number < pageCount ? `p${number + 1}` : null,
    previousCursor: number > 1 ? `p${number - 1}` : null,
    resultsRefreshed: false,
  };
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
  'Audit.Read',
  'Audit.ReadSearchText',
  'Workspace.ManageUsers',
  'Workspace.ManageSecurity',
  'Workspace.ManageFields',
  'Workspace.RequestDeletion',
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
};

const CAPABILITIES = {
  sortable: true,
  filterable: true,
  rangeable: true,
  aggregatable: true,
  fullText: true,
  wildcard: true,
  leadingWildcard: false,
  highlightable: true,
  exists: true,
};

/** `GET /api/v1/workspaces/{id}/fields` items: the grid's structural fields and a custom field of the review template. */
export const FIELDS = [
  ...SYSTEM_FIELDS,
  {
    fieldId: 1000,
    displayName: 'Responsiveness',
    queryName: 'responsiveness',
    type: 'singleChoice',
    storage: 'coding',
    multiValue: false,
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    capabilities: CAPABILITIES,
    reducedCapabilities: false,
    choices: [
      { choiceId: 1, name: 'Responsive', isActive: true },
      { choiceId: 2, name: 'Not Responsive', isActive: true },
    ],
  },
];

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
  const control: MockControl = {
    unhandled,
    audit,
    expireSearches: () => (expired = true),
    removeDocuments: (...numbers) => numbers.forEach((n) => removed.add(n)),
  };

  await page.route(/\/(api|bff)\//, (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/v1/me') {
      return signedIn ? json(route, principal) : route.fulfill(problem(401, 'Unauthorized'));
    }
    const method = route.request().method();
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
      const body = route.request().postDataJSON() as { pageSize?: number; query?: string };
      pageSize = Number(body?.pageSize ?? 100);
      // A fielded query (the filter row) narrows the list, so filtering is visible end to end.
      total = body?.query?.includes(':') ? Math.min(documents, 12) : documents;
      expired = false;
      lastQuery = String(body?.query ?? '');
      return json(route, searchPage(matching(), pageSize, 1, lastQuery));
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
      return json(route, searchPage(docs, pageSize, n, lastQuery));
    }
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
      if (content[2] === 'coding' && method === 'GET') {
        // E10-T01: current coding with the DocumentVersion as ETag.
        const n = Number(content[1]);
        return route.fulfill({
          json: {
            documentId,
            documentVersion: '3',
            projectedVersion: '3',
            layoutId: null,
            lastEditor: null,
            indexingState: 'indexed',
            // Responsiveness (field 1000) = Responsive (choice 1) on every third document.
            fields:
              n % 3 === 0
                ? [
                    {
                      fieldId: 1000,
                      value: 1,
                      editable: true,
                      isSecurityAffecting: false,
                      changedAtVersion: '3',
                      changedBy: null,
                      changedAt: null,
                    },
                  ]
                : [],
          },
          headers: { ETag: '"3"' },
        });
      }
    }
    if (signedIn && path === '/api/v1/workspaces')
      return json(route, {
        items: WORKSPACES,
        nextCursor: null,
        total: { value: WORKSPACES.length, relation: 'eq' },
      });
    const ws = WORKSPACES.find((w) => path === `/api/v1/workspaces/${w.workspaceId}`);
    if (signedIn && ws)
      return json(route, {
        ...ws,
        permissions,
        breakGlassActive: false,
        storageProfile: 'default',
        version: 1,
        searchPlacement: null,
      });
    unhandled.push(`${route.request().method()} ${path}`);
    return route.fulfill(problem(404, 'Not found'));
  });
  return control;
}
