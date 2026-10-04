import type { Page, Route } from '@playwright/test';

/**
 * In-browser stand-in for the BFF and API, mirroring src/app/core/api/fake-api.testing.ts: answers the routes the
 * shell needs (`/api/v1/me`, the user's preferences, the workspace directory) and 404 problem details for anything else, so a page that
 * starts calling a new endpoint fails visibly instead of hanging.
 */
export interface MockApiOptions {
  signedIn?: boolean;
  /** Effective permissions in every workspace; defaults to all (so admin routes are reachable). */
  permissions?: readonly string[];
  /** The signed-in user's stored preferences (`/api/v1/me/preferences`); starts empty. */
  preferences?: Record<string, unknown>;
  /** Documents every search of the mock finds (the document list); default 250. */
  documents?: number;
}

/** A search result page of the document list (`SearchResultPage`); cursors encode the page number (`p<n>`). */
function searchPage(total: number, pageSize: number, number: number) {
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const exact = total <= 10_000;
  const first = (number - 1) * pageSize + 1;
  const count = Math.max(0, Math.min(pageSize, total - first + 1));
  const items = Array.from({ length: count }, (_, i) => {
    const n = first + i;
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
      fileType: attachment ? 'PDF Document' : 'Email Message',
      mimeType: attachment ? 'application/pdf' : 'application/vnd.ms-outlook',
      pageCount: (n % 7) + 1,
      snippets: [],
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

const FIELDS = [
  ['controlnumber', 'Control Number'],
  ['date', 'Document Date'],
  ['filename', 'File Name'],
  ['filetype', 'File Type'],
  ['extension', 'File Extension'],
  ['filesize', 'File Size'],
  ['pagecount', 'Page Count'],
].map(([queryName, displayName], i) => ({
  fieldId: i + 1,
  queryName,
  displayName,
  type: 'keyword',
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
    wildcard: true,
    leadingWildcard: false,
    rangeable: false,
    exists: true,
  },
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

function problem(status: number, title: string) {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title, status }),
  };
}

/** Installs the mock; returns the list it fills with requests it had no answer for. */
export async function mockApi(page: Page, options: MockApiOptions = {}): Promise<string[]> {
  const { signedIn = true, permissions = ALL_PERMISSIONS, documents = 250 } = options;
  let pageSize = 100;
  // The "server" copy of the preferences outlives reloads of the page, like the real profile.
  const preferences: Record<string, unknown> = { ...options.preferences };
  const json = (route: Route, body: unknown) => route.fulfill({ json: body });
  const unhandled: string[] = [];

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
    if (signedIn && /^\/api\/v1\/workspaces\/[^/]+\/fields$/.test(path) && method === 'GET') {
      return json(route, {
        items: FIELDS,
        nextCursor: null,
        total: { value: FIELDS.length, relation: 'eq' },
      });
    }
    if (signedIn && /^\/api\/v1\/workspaces\/[^/]+\/searches$/.test(path) && method === 'POST') {
      pageSize = Number((route.request().postDataJSON() as { pageSize?: number })?.pageSize ?? 100);
      return json(route, searchPage(documents, pageSize, 1));
    }
    if (signedIn && /^\/api\/v1\/workspaces\/[^/]+\/searches\/[^/]+\/pages$/.test(path)) {
      const cursor = url.searchParams.get('cursor');
      const last = url.searchParams.get('last') === 'true';
      const n = cursor
        ? Number(cursor.slice(1))
        : last
          ? Math.max(1, Math.ceil(documents / pageSize))
          : Number(url.searchParams.get('page') ?? 1);
      return json(route, searchPage(documents, pageSize, n));
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
  return unhandled;
}
