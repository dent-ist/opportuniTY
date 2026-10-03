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

export const WORKSPACES = [
  { workspaceId: 'ws-1', name: 'Acme v. Widget', matterNumber: 'M-1001' },
  { workspaceId: 'ws-2', name: 'Beta Holdings', matterNumber: 'M-1002' },
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
  const { signedIn = true, permissions = ALL_PERMISSIONS } = options;
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
    if (signedIn && path === '/api/v1/workspaces')
      return json(route, { items: WORKSPACES, nextCursor: null });
    const ws = WORKSPACES.find((w) => path === `/api/v1/workspaces/${w.workspaceId}`);
    if (signedIn && ws) return json(route, { ...ws, permissions });
    unhandled.push(`${route.request().method()} ${path}`);
    return route.fulfill(problem(404, 'Not found'));
  });
  return unhandled;
}
