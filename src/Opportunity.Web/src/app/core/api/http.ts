import {
  HttpInterceptorFn,
  provideHttpClient,
  withInterceptors,
  withXsrfConfiguration,
} from '@angular/common/http';
import { EnvironmentProviders, Provider, inject, makeEnvironmentProviders } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import {
  DEFAULT_SESSION_CONFIG,
  SessionConfig,
  SESSION_CONFIG,
  SessionService,
} from '../session/session';
import { acknowledgmentInterceptor } from '../workspace/acknowledgment';
import { workspaceBoundaryInterceptor } from '../workspace/workspace-context';
import { provideApiConfiguration } from './generated';
import { toApiError } from './problem-details';

/** Any 401 means the BFF session is gone: flip the session signal so the shell can prompt to sign in. */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionService);
  const { principalUrl } = inject(SESSION_CONFIG);
  return next(req).pipe(
    catchError((e: unknown) => {
      const error = toApiError(e);
      if (error.status === 401 && req.url !== principalUrl) session.markExpired();
      return throwError(() => error);
    }),
  );
};

/** Normalises every HTTP failure to `ApiError` (RFC 9457 problem details, ADR-019 §2.4). */
export const problemDetailsInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(catchError((e: unknown) => throwError(() => toApiError(e))));

/**
 * HTTP stack for the API (ADR-018 §3–§5): same-origin relative URLs, cookie session, Angular XSRF support
 * against ASP.NET Core antiforgery (the BFF's readable `__Host-opp-xsrf` cookie is echoed in `X-XSRF-TOKEN` on
 * every unsafe method), problem-details normalisation, the acknowledgment redirect (E20-T03) and the workspace
 * boundary check. XHR backend (not fetch) because
 * native/load-file uploads need upload progress events.
 */
export function provideOpportunityHttp(
  session: SessionConfig = DEFAULT_SESSION_CONFIG,
): (Provider | EnvironmentProviders)[] {
  return [
    provideHttpClient(
      withXsrfConfiguration({
        cookieName: session.xsrfCookieName,
        headerName: session.xsrfHeaderName,
      }),
      withInterceptors([
        problemDetailsInterceptor,
        sessionInterceptor,
        acknowledgmentInterceptor,
        workspaceBoundaryInterceptor,
      ]),
    ),
    makeEnvironmentProviders([
      { provide: SESSION_CONFIG, useValue: session },
      provideApiConfiguration(''),
    ]),
  ];
}
