import { Injectable, inject, signal } from '@angular/core';
import { Title } from '@angular/platform-browser';
import {
  ActivatedRouteSnapshot,
  BaseRouteReuseStrategy,
  CanActivateFn,
  RedirectCommand,
  ResolveFn,
  Router,
  RouterStateSnapshot,
  TitleStrategy,
} from '@angular/router';
import { ApiError, toApiError } from '../core/api/problem-details';
import { SessionService } from '../core/session/session';
import { Workspace, WorkspaceDirectory } from '../core/workspace/workspace-api';
import { WORKSPACE_DATA } from '../core/workspace/workspace-context';

export const PRODUCT_NAME = 'opportuniTY';

/** In-app paths of the shell's own pages. */
export const SHELL_PATHS = {
  workspaces: '/workspaces',
  signIn: '/sign-in',
  notAvailable: '/not-available',
  error: '/error',
  sessionError: '/session-error',
  about: '/about',
} as const;

/** Route data flag: the route owns a workspace scope keyed by its `:workspaceId` parameter. */
export const WORKSPACE_SCOPE = 'workspaceScope';

/** The failure behind the error page, with the URL to retry. */
@Injectable({ providedIn: 'root' })
export class ShellErrors {
  private readonly _last = signal<{ error: ApiError; url: string } | null>(null);
  readonly last = this._last.asReadonly();

  report(error: ApiError, url: string): void {
    this._last.set({ error, url });
  }
}

/**
 * Leaving one workspace for another must not reuse anything: by default Angular keeps a routed component
 * when only its parameters change, which would carry workspace state across the boundary.
 */
export class WorkspaceRouteReuseStrategy extends BaseRouteReuseStrategy {
  override shouldReuseRoute(future: ActivatedRouteSnapshot, curr: ActivatedRouteSnapshot): boolean {
    if (future.routeConfig !== curr.routeConfig) return false;
    if (!future.routeConfig?.data?.[WORKSPACE_SCOPE]) return true;
    return future.paramMap.get('workspaceId') === curr.paramMap.get('workspaceId');
  }
}

/** "Documents · ACME v. Widget · opportuniTY" (WCAG 2.4.2): page, workspace, product. */
@Injectable({ providedIn: 'root' })
export class ShellTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    let workspace: Workspace | undefined;
    for (let r: ActivatedRouteSnapshot | null = snapshot.root; r; r = r.firstChild) {
      workspace = (r.data[WORKSPACE_DATA] as Workspace | undefined) ?? workspace;
    }
    const parts = [this.buildTitle(snapshot), workspace?.name, PRODUCT_NAME];
    this.title.setTitle(parts.filter((p) => !!p).join(' · '));
  }
}

/** The `:workspaceId` of the nearest route that has one. */
export function workspaceIdOf(route: ActivatedRouteSnapshot): string {
  for (let r: ActivatedRouteSnapshot | null = route; r; r = r.parent) {
    const id = r.paramMap.get('workspaceId');
    if (id) return id;
  }
  return '';
}

/** 400/403/404: the resource is missing, malformed or forbidden. All three look the same (Q-13). */
export function isNoAccess(error: ApiError): boolean {
  return error.status === 400 || error.status === 403 || error.status === 404;
}

/**
 * Where a failed load of a routed resource leads: the single "Not available" page for no access and not
 * found (never revealing which), the error page for anything else. The address bar keeps the requested
 * URL, so reload and bookmarks still point at it.
 * A 401 cancels the navigation; the shell is already asking the user to sign in again.
 */
export function redirectForFailure(
  router: Router,
  errors: ShellErrors,
  failure: unknown,
  url: string,
): RedirectCommand | false {
  const error = toApiError(failure);
  if (error.status === 401) return false;
  if (isNoAccess(error)) {
    return new RedirectCommand(router.parseUrl(SHELL_PATHS.notAvailable), { browserUrl: url });
  }
  errors.report(error, url);
  return new RedirectCommand(router.parseUrl(SHELL_PATHS.error), { browserUrl: url });
}

/** Signed-in users only; loads the principal from the BFF on first use. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionService);
  const router = inject(Router);
  const errors = inject(ShellErrors);
  let status = session.status();
  if (status === 'unknown') {
    try {
      status = await session.refresh();
    } catch (e) {
      errors.report(toApiError(e), state.url);
      return new RedirectCommand(router.parseUrl(SHELL_PATHS.sessionError), {
        browserUrl: state.url,
      });
    }
  }
  if (status === 'authenticated') return true;
  if (status === 'expired') return false;
  return router.createUrlTree([SHELL_PATHS.signIn], { queryParams: { returnUrl: state.url } });
};

/** The workspace exists and the user is a member; otherwise "Not available". */
export const workspaceGuard: CanActivateFn = async (route, state) => {
  const directory = inject(WorkspaceDirectory);
  const router = inject(Router);
  const errors = inject(ShellErrors);
  try {
    await directory.get(workspaceIdOf(route));
    return true;
  } catch (e) {
    return redirectForFailure(router, errors, e, state.url);
  }
};

/** A section or admin area is shown only with its permission; a deep link without it is "Not available". */
export function requirePermission(permission: string): CanActivateFn {
  return async (route, state) => {
    const directory = inject(WorkspaceDirectory);
    const router = inject(Router);
    const errors = inject(ShellErrors);
    try {
      const workspace = await directory.get(workspaceIdOf(route));
      if (workspace.permissions.includes(permission)) return true;
      return redirectForFailure(router, errors, new ApiError(404, { status: 404 }), state.url);
    } catch (e) {
      return redirectForFailure(router, errors, e, state.url);
    }
  };
}

export const workspaceResolver: ResolveFn<Workspace> = (route) =>
  inject(WorkspaceDirectory).get(workspaceIdOf(route));
