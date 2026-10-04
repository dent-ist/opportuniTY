import type { Page, Route } from '@playwright/test';

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
}

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

/** `GET /api/v1/workspaces/{id}/fields` items: a structural field and the custom fields of the review template. */
export const FIELDS = [
  {
    fieldId: 1,
    displayName: 'Control Number',
    queryName: 'controlnumber',
    type: 'keyword',
    storage: 'column',
    multiValue: false,
    isSystem: true,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    capabilities: CAPABILITIES,
    reducedCapabilities: false,
    choices: null,
  },
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
] as const;

function problem(status: number, title: string) {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title, status }),
  };
}

/** Installs the mock; returns the list it fills with requests it had no answer for. */
export async function mockApi(page: Page, options: MockApiOptions = {}): Promise<string[]> {
  const { signedIn = true, permissions = ALL_PERMISSIONS } = options;
  // The "server" copy of the preferences outlives reloads of the page, like the real profile.
  const preferences: Record<string, unknown> = { ...options.preferences };
  // Likewise the user's query history per workspace (newest first, distinct, at most 50).
  const history = new Map<string, { query: string; ranAt: string }[]>();
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
