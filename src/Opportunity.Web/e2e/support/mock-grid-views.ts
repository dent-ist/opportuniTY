import type { Route } from '@playwright/test';

/**
 * Document-list views for the e2e mock API (E16-T09), in the shapes of `…/grid-views` (OpenAPI): the visible views,
 * create / replace / delete with If-Match (shared ones need `View.ManageShared`), and the user's layout
 * (`GET/PUT …/grid-views/layout`), which outlives page reloads like the real server copy.
 */

interface View {
  viewId: string;
  name: string;
  visibility: 'personal' | 'shared';
  owner: { userId: string; displayName: string };
  columns: { field: string; width?: number | null; pinned?: boolean }[];
  sort: { field: string; direction: string }[];
  modifiedAt: string;
  version: number;
  canEdit: boolean;
}

export interface GridLayoutBody {
  viewId: string | null;
  columns: { field: string; width?: number | null; pinned?: boolean }[] | null;
  sort: { field: string; direction: string }[] | null;
}

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };

export class GridViewsMock {
  /** Views of the workspace: one shared view with a coding column. */
  readonly views: View[];
  /** Layouts saved (`PUT …/grid-views/layout`), oldest first. */
  readonly layouts: GridLayoutBody[] = [];
  /** View writes received. */
  readonly writes: {
    method: string;
    body: Record<string, unknown> | null;
    ifMatch: string | null;
  }[] = [];
  private layout: GridLayoutBody = { viewId: null, columns: null, sort: null };
  private next = 1;

  constructor(private readonly manageShared: boolean) {
    this.views = [
      {
        viewId: 'view-shared-1',
        name: 'First pass review',
        visibility: 'shared',
        owner: { userId: 'user-2', displayName: 'Jamie Lee' },
        columns: [
          { field: 'responsiveness', pinned: true },
          { field: 'filename' },
          { field: 'date' },
        ],
        sort: [{ field: 'documentDate', direction: 'desc' }],
        modifiedAt: '2026-10-04T09:00:00Z',
        version: 1,
        canEdit: manageShared,
      },
    ];
  }

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const rest = /^\/api\/v1\/workspaces\/[^/]+\/grid-views(\/.*)?$/.exec(path);
    if (!rest) return undefined;
    const sub = rest[1] ?? '';
    const request = route.request();
    const body = () => (request.postDataJSON() ?? {}) as Record<string, unknown>;
    const json = (status: number, data: unknown) => route.fulfill({ status, json: data });
    const problem = (status: number, title: string) =>
      route.fulfill({
        status,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title, status }),
      });

    if (sub === '/layout') {
      if (method === 'GET') return json(200, { ...this.layout, modifiedAt: null });
      if (method === 'PUT') {
        this.layout = body() as unknown as GridLayoutBody;
        this.layouts.push(this.layout);
        return json(200, { ...this.layout, modifiedAt: new Date().toISOString() });
      }
    }
    if (sub === '' && method === 'GET') return json(200, { items: this.views });
    if (sub === '' && method === 'POST') {
      const b = body();
      this.writes.push({ method, body: b, ifMatch: null });
      if (b['visibility'] === 'shared' && !this.manageShared) return problem(403, 'Forbidden');
      const view: View = {
        viewId: `view-${this.next++}`,
        name: String(b['name']),
        visibility: b['visibility'] === 'shared' ? 'shared' : 'personal',
        owner: ME,
        columns: (b['columns'] as View['columns']) ?? [],
        sort: (b['sort'] as View['sort']) ?? [],
        modifiedAt: new Date().toISOString(),
        version: 1,
        canEdit: true,
      };
      this.views.push(view);
      return json(201, view);
    }
    const id = /^\/([^/]+)$/.exec(sub)?.[1];
    const view = this.views.find((v) => v.viewId === id);
    if (!view) return problem(404, 'Not found');
    const ifMatch = request.headers()['if-match'] ?? null;
    if (method === 'PUT' || method === 'DELETE') {
      this.writes.push({ method, body: method === 'PUT' ? body() : null, ifMatch });
      if (!view.canEdit) return problem(403, 'Forbidden');
      if (ifMatch !== `"${view.version}"`) return problem(412, 'Precondition failed');
      if (method === 'DELETE') {
        this.views.splice(this.views.indexOf(view), 1);
        return route.fulfill({ status: 204 });
      }
      const b = body();
      Object.assign(view, {
        name: String(b['name']),
        visibility: b['visibility'] === 'shared' ? 'shared' : 'personal',
        columns: b['columns'] ?? [],
        sort: b['sort'] ?? [],
        version: view.version + 1,
      });
      return json(200, view);
    }
    return json(200, view);
  }
}
