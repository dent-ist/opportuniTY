import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/**
 * Workspace directory contract the shell relies on. The API does not publish these routes yet (workspace
 * listing and the caller's effective permissions arrive with E05-T02 / E04-T02), so the types are written
 * by hand from ADR-019 conventions; switch to the generated client once the OpenAPI document has them.
 *
 * - `GET /api/v1/workspaces?limit=&cursor=` → cursor page of the workspaces the caller is a member of.
 * - `GET /api/v1/workspaces/{workspaceId}` → the workspace with the caller's effective permissions;
 *   404 when it does not exist **or** the caller has no access (ADR-019 §2.3, no enumeration).
 */
export const WORKSPACES_URL = '/api/v1/workspaces';

export interface WorkspaceSummary {
  workspaceId: string;
  name: string;
  matterNumber?: string | null;
}

export interface Workspace extends WorkspaceSummary {
  /** Effective permissions of the caller in this workspace (closed catalogue, E05-T02). */
  permissions: readonly string[];
}

export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
  total?: { value: number; relation: 'eq' | 'gte' };
}

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

  async list(cursor?: string | null, limit = 100): Promise<CursorPage<WorkspaceSummary>> {
    const params: Record<string, string | number> = { limit };
    if (cursor) params['cursor'] = cursor;
    const page = await firstValueFrom(
      this.http.get<CursorPage<WorkspaceSummary>>(WORKSPACES_URL, { params }),
    );
    this._known.update((known) => {
      const next = new Map(known);
      for (const ws of page.items) next.set(ws.workspaceId, ws);
      return next;
    });
    return page;
  }

  /** Forget cached workspaces (sign-out, session end). */
  clear(): void {
    this.last = undefined;
    this._known.set(new Map());
  }
}
