import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiConfiguration } from '../api/generated/api-configuration';
import { createWorkspace } from '../api/generated/fn/workspaces/create-workspace';
import { listWorkspaces } from '../api/generated/fn/workspaces/list-workspaces';
import type {
  CursorPageOfWorkspaceSummary,
  WorkspaceResource,
  WorkspaceSummary,
  WorkspaceWrite,
} from '../api/generated/models';

/**
 * Workspace directory contract the shell relies on (generated client, E04-T05).
 *
 * - `GET /api/v1/workspaces/{workspaceId}` (`WorkspaceResource`) → the workspace with the caller's effective
 *   permissions; 404 when it does not exist **or** the caller has no access (no enumeration).
 * - `GET /api/v1/workspaces?limit=&cursor=` (`CursorPageOfWorkspaceSummary`) → cursor page of the workspaces the
 *   caller is a member of, by name. A workspace that would answer 404 is never listed.
 * - `POST /api/v1/workspaces` (`WorkspaceWrite`) → 201 `WorkspaceResource`; needs `Installation.ManageWorkspaces` and an
 *   MFA session (403 `step-up-required` with `stepUpUrl` otherwise). The creator becomes its Workspace Admin.
 * - `PUT /api/v1/workspaces/{workspaceId}` (`WorkspaceWrite`, `If-Match` = version) → the updated `WorkspaceResource`;
 *   needs `Workspace.ManageSecurity`.
 */
export const WORKSPACES_URL = '/api/v1/workspaces';

export type { WorkspaceSummary, WorkspaceWrite };

/** Effective permissions are the closed catalogue names (docs/security/permission-matrix.md). */
export type Workspace = WorkspaceResource;

export type WorkspacePage = CursorPageOfWorkspaceSummary;

export function workspaceUrl(workspaceId: string): string {
  return `${WORKSPACES_URL}/${encodeURIComponent(workspaceId)}`;
}

/**
 * Reads workspaces for the shell. `get` memoises the last workspace so the route guards and resolver of
 * one navigation share a single request; entering another workspace replaces it.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceDirectory {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private last?: { id: string; workspace: Promise<Workspace> };
  private readonly _known = signal<ReadonlyMap<string, WorkspaceSummary>>(new Map());

  /** Workspaces seen in list pages this session, for the switcher's recent list. */
  readonly known = this._known.asReadonly();

  get(workspaceId: string): Promise<Workspace> {
    if (this.last?.id === workspaceId) return this.last.workspace;
    const workspace = firstValueFrom(this.http.get<Workspace>(workspaceUrl(workspaceId)));
    const entry = { id: workspaceId, workspace };
    this.last = entry;
    // A failed load is not cached: the next navigation asks again.
    workspace.catch(() => {
      if (this.last === entry) this.last = undefined;
    });
    return workspace;
  }

  /** Reads the workspace from the API again (e.g. before editing its settings), replacing the cached copy. */
  refresh(workspaceId: string): Promise<Workspace> {
    if (this.last?.id === workspaceId) this.last = undefined;
    return this.get(workspaceId);
  }

  async list(cursor?: string | null, limit = 100): Promise<WorkspacePage> {
    const response = await firstValueFrom(
      listWorkspaces(this.http, this.rootUrl, { limit, cursor: cursor ?? undefined }),
    );
    const page = response.body;
    this._known.update((known) => {
      const next = new Map(known);
      for (const ws of page.items) next.set(ws.workspaceId, ws);
      return next;
    });
    return page;
  }

  /** `POST /api/v1/workspaces`: creates a workspace; the caller becomes its Workspace Admin. */
  async create(write: WorkspaceWrite): Promise<Workspace> {
    const response = await firstValueFrom(
      createWorkspace(this.http, this.rootUrl, { body: write }),
    );
    this.remember(response.body);
    return response.body;
  }

  /** `PUT /api/v1/workspaces/{id}` with `If-Match` on the version that was read; 412 when someone changed it since. */
  async update(workspace: Workspace, write: WorkspaceWrite): Promise<Workspace> {
    const updated = await firstValueFrom(
      this.http.put<Workspace>(workspaceUrl(workspace.workspaceId), write, {
        headers: { 'If-Match': `"${workspace.version}"` },
      }),
    );
    this.remember(updated);
    return updated;
  }

  /** Makes `workspace` the cached copy (after a create or an update), so guards and the switcher see the new values. */
  private remember(workspace: Workspace): void {
    this.last = { id: workspace.workspaceId, workspace: Promise.resolve(workspace) };
    this._known.update((known) => {
      const next = new Map(known);
      const { workspaceId, name, matterNumber, displayTimeZone, status, createdAt } = workspace;
      next.set(workspaceId, {
        workspaceId,
        name,
        matterNumber,
        displayTimeZone,
        status,
        createdAt,
      });
      return next;
    });
  }

  /** Forget cached workspaces (sign-out, session end). */
  clear(): void {
    this.last = undefined;
    this._known.set(new Map());
  }
}
