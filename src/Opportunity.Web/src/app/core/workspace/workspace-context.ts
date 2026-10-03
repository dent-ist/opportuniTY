import { HttpInterceptorFn } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { throwError } from 'rxjs';
import { PreferenceStorage } from '../preferences/preference-storage';
import { ApiError } from '../api/problem-details';
import { SessionService } from '../session/session';
import { WORKSPACES_URL, Workspace, workspaceUrl } from './workspace-api';

/** Route data key under which the workspace route resolves the `Workspace`. */
export const WORKSPACE_DATA = 'workspace';

/**
 * The workspace the router currently shows, for root-level consumers (header, title, HTTP boundary).
 * Set and cleared by `WorkspaceContext`; it holds the descriptor only, never workspace data.
 */
@Injectable({ providedIn: 'root' })
export class ActiveWorkspace {
  private readonly _current = signal<Workspace | null>(null);
  readonly current = this._current.asReadonly();
  readonly id = computed(() => this._current()?.workspaceId ?? null);

  enter(workspace: Workspace): void {
    this._current.set(workspace);
  }

  leave(workspaceId: string): void {
    if (this._current()?.workspaceId === workspaceId) this._current.set(null);
  }
}

/** Ids (never names or data) of the user's most recently opened workspaces, newest first. */
@Injectable({ providedIn: 'root' })
export class RecentWorkspaces {
  static readonly MAX = 5;
  private readonly storage = inject(PreferenceStorage);
  private readonly session = inject(SessionService);
  private readonly key = computed(() => {
    const userId = this.session.principal()?.userId;
    return userId ? `recentWorkspaces.${userId}` : null;
  });
  private readonly visited = signal<readonly string[] | null>(null);

  readonly ids = computed<readonly string[]>(() => {
    const visited = this.visited();
    if (visited) return visited;
    const key = this.key();
    const stored = key ? this.storage.read<unknown>(key) : undefined;
    return Array.isArray(stored) ? stored.filter((id) => typeof id === 'string') : [];
  });

  visit(workspaceId: string): void {
    const ids = [workspaceId, ...this.ids().filter((id) => id !== workspaceId)].slice(
      0,
      RecentWorkspaces.MAX,
    );
    this.visited.set(ids);
    const key = this.key();
    if (key) this.storage.write(key, ids);
  }
}

/**
 * Workspace scope (ADR-018 §2.3). Provided by the workspace shell component, which the router re-creates
 * whenever `:workspaceId` changes (`WorkspaceRouteReuseStrategy`), so this service, every workspace-scoped
 * store provided beside it or by a feature component, and all their state are destroyed on a switch.
 *
 * The route parameter is the only source of the workspace id: feature code builds API URLs with
 * `apiUrl()` (or passes `workspaceId` to the generated client) and never stores the id elsewhere.
 */
@Injectable()
export class WorkspaceContext {
  private readonly route = inject(ActivatedRoute);
  readonly workspaceId: string = this.route.snapshot.paramMap.get('workspaceId') ?? '';
  readonly workspace: Workspace = this.route.snapshot.data[WORKSPACE_DATA] as Workspace;
  private readonly granted = new Set(this.workspace.permissions);

  constructor() {
    const active = inject(ActiveWorkspace);
    active.enter(this.workspace);
    inject(RecentWorkspaces).visit(this.workspaceId);
    inject(DestroyRef).onDestroy(() => active.leave(this.workspaceId));
  }

  can(permission: string): boolean {
    return this.granted.has(permission);
  }

  /** `/api/v1/workspaces/{workspaceId}/…segments`, each segment URL-encoded. */
  apiUrl(...segments: string[]): string {
    return [workspaceUrl(this.workspaceId), ...segments.map(encodeURIComponent)].join('/');
  }
}

/** Client-side code of a request refused by `workspaceBoundaryInterceptor`. */
export const WORKSPACE_MISMATCH = 'workspace-mismatch';

const WORKSPACE_SCOPED = new RegExp(`^${WORKSPACES_URL}/([^/?#]+)/`);

/**
 * Defence in depth for the hard workspace boundary (baseline §2.3): a workspace-scoped API call
 * (`/api/v1/workspaces/{id}/…`) is sent only for the workspace in the current route. Calls from a
 * workspace the user has just left, or from outside any workspace, fail without reaching the network.
 */
export const workspaceBoundaryInterceptor: HttpInterceptorFn = (req, next) => {
  const match = WORKSPACE_SCOPED.exec(req.url);
  if (match) {
    const active = inject(ActiveWorkspace).id();
    if (decodeURIComponent(match[1]) !== active) {
      return throwError(
        () =>
          new ApiError(0, {
            title: 'Request belongs to another workspace',
            code: WORKSPACE_MISMATCH,
          }),
      );
    }
  }
  return next(req);
};
