import { HttpInterceptorFn } from '@angular/common/http';
import { Injector, inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { toApiError } from '../api/problem-details';
import { WORKSPACES_URL, WorkspaceDirectory } from './workspace-api';

/**
 * Reviewer attestation and protective-order acknowledgment (E20-T03). While a workspace publishes acknowledgment text
 * the member has not accepted in its current version, the API answers every workspace route except reading and
 * accepting the text (and the workspace itself) with 403 `acknowledgment-required`. The web app sends the member to the
 * acknowledgment page instead: before entering the workspace (`acknowledgmentGuard`, from `acknowledgmentPending` on
 * the workspace) and whenever a call is refused mid-session, e.g. after an administrator published a new version
 * (`acknowledgmentInterceptor`).
 */
export const ACKNOWLEDGMENT_REQUIRED = 'acknowledgment-required';

/** In-app path of a workspace's acknowledgment page. */
export function acknowledgmentPath(workspaceId: string): string {
  return `/w/${encodeURIComponent(workspaceId)}/acknowledgment`;
}

/**
 * Where to continue after accepting: the requested page of the same workspace, never another workspace, the
 * acknowledgment page itself or another site.
 */
export function continueUrl(workspaceId: string, returnUrl: string | null | undefined): string {
  const base = `/w/${encodeURIComponent(workspaceId)}`;
  if (
    returnUrl &&
    returnUrl.startsWith(base + '/') &&
    !returnUrl.startsWith(acknowledgmentPath(workspaceId)) &&
    !returnUrl.includes('//')
  ) {
    return returnUrl;
  }
  return base + '/documents';
}

/** On the workspace route: a member who still has to accept the acknowledgment goes to its page first. */
export const acknowledgmentGuard: CanActivateFn = async (route, state) => {
  const id = route.paramMap.get('workspaceId') ?? '';
  const router = inject(Router);
  try {
    const workspace = await inject(WorkspaceDirectory).get(id);
    if (!workspace.acknowledgmentPending) return true;
  } catch {
    // The workspace guard reports load failures.
    return true;
  }
  return router.createUrlTree([acknowledgmentPath(id)], { queryParams: { returnUrl: state.url } });
};

const WORKSPACE_SCOPED = new RegExp(`^${WORKSPACES_URL}/([^/?#]+)/`);

/**
 * A workspace call refused with `acknowledgment-required` opens the acknowledgment page (keeping where the member
 * was, to return after accepting). The error still reaches the caller.
 */
export const acknowledgmentInterceptor: HttpInterceptorFn = (req, next) => {
  const injector = inject(Injector);
  return next(req).pipe(
    catchError((e: unknown) => {
      const error = toApiError(e);
      const match = WORKSPACE_SCOPED.exec(req.url);
      if (error.status === 403 && error.code === ACKNOWLEDGMENT_REQUIRED && match) {
        const id = decodeURIComponent(match[1]);
        const router = injector.get(Router, null);
        if (router && !router.url.startsWith(acknowledgmentPath(id))) {
          void router.navigate([acknowledgmentPath(id)], {
            queryParams: { returnUrl: router.url },
          });
        }
      }
      return throwError(() => error);
    }),
  );
};
