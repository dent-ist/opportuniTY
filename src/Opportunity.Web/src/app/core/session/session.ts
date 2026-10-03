import { HttpClient } from '@angular/common/http';
import { DOCUMENT, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { MeResponse } from '../api/generated/models';
import { ApiError, toApiError } from '../api/problem-details';

/**
 * BFF contract the SPA relies on (ADR-018 §4, ADR-015 D4). The SPA never sees tokens: it holds no
 * credentials beyond the HttpOnly session cookie the browser sends automatically. Paths are owned by the
 * BFF (#46, E05-T01) and configured here in one place.
 */
export interface SessionConfig {
  /** GET → 200 current principal, or 401 problem details (never a redirect) when there is no session. */
  readonly principalUrl: string;
  /** Top-level navigation target that starts the OIDC code + PKCE flow; takes a relative `returnUrl`. */
  readonly loginUrl: string;
  /** POST (anti-forgery protected) → revokes the server session; responds 200 `{ endSessionUrl }` (may be null). */
  readonly logoutUrl: string;
  /** Readable anti-forgery cookie set by the BFF and the header it expects on unsafe methods. */
  readonly xsrfCookieName: string;
  readonly xsrfHeaderName: string;
}

export const DEFAULT_SESSION_CONFIG: SessionConfig = {
  principalUrl: '/api/v1/me',
  loginUrl: '/bff/login',
  logoutUrl: '/bff/logout',
  xsrfCookieName: '__Host-opp-xsrf',
  xsrfHeaderName: 'X-XSRF-TOKEN',
};

export const SESSION_CONFIG = new InjectionToken<SessionConfig>('SESSION_CONFIG', {
  factory: () => DEFAULT_SESSION_CONFIG,
});

/** `GET /api/v1/me` (generated from the OpenAPI document). */
export type SessionPrincipal = MeResponse;

/** `POST /bff/logout` body. The BFF route is outside the OpenAPI document, so it is typed here. */
export interface LogoutResponse {
  endSessionUrl?: string | null;
}

export type SessionStatus = 'unknown' | 'authenticated' | 'anonymous' | 'expired';

/** Session state as signals. `expired` is set by `sessionInterceptor` on any 401 from the API. */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SESSION_CONFIG);
  private readonly document = inject(DOCUMENT);

  private readonly _status = signal<SessionStatus>('unknown');
  private readonly _principal = signal<SessionPrincipal | null>(null);

  readonly status = this._status.asReadonly();
  readonly principal = this._principal.asReadonly();
  readonly isAuthenticated = computed(() => this._status() === 'authenticated');

  async refresh(): Promise<SessionStatus> {
    try {
      const principal = await firstValueFrom(
        this.http.get<SessionPrincipal>(this.config.principalUrl),
      );
      this._principal.set(principal);
      this._status.set('authenticated');
    } catch (e) {
      const error = toApiError(e);
      if (error.status !== 401) throw error;
      this._principal.set(null);
      // A 401 after a successful sign-in means the session ended (idle/absolute timeout, back-channel logout).
      this._status.update((s) =>
        s === 'authenticated' || s === 'expired' ? 'expired' : 'anonymous',
      );
    }
    return this._status();
  }

  /** Called on any 401 from the API. Workspace-scoped client state must be discarded by the shell. */
  markExpired(): void {
    if (this._status() !== 'anonymous') this._status.set('expired');
  }

  /** Full-page navigation to the BFF login; `returnUrl` must be an in-app relative path. */
  login(returnUrl = this.currentPath()): void {
    const safe = returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/';
    this.document.location.assign(`${this.config.loginUrl}?returnUrl=${encodeURIComponent(safe)}`);
  }

  /**
   * Ends the server session, then leaves the app with a full-page navigation (which also drops every piece
   * of client state): to the IdP's end-session URL when the BFF returns one, otherwise to the sign-in page.
   */
  async logout(): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<LogoutResponse | null>(this.config.logoutUrl, null),
    ).catch((e: unknown) => {
      const error = toApiError(e);
      if (error.status === 401) return null;
      throw error;
    });
    this._principal.set(null);
    this._status.set('anonymous');
    this.document.location.assign(safeEndSessionUrl(response?.endSessionUrl) ?? SIGNED_OUT_PATH);
  }

  private currentPath(): string {
    const { pathname, search, hash } = this.document.location;
    return `${pathname}${search}${hash}`;
  }
}

/** Where the app lands after signing out when the IdP has no end-session endpoint. */
export const SIGNED_OUT_PATH = '/sign-in?signedOut=true';

/** Only http(s) URLs or same-origin paths: never `javascript:` or other schemes. */
function safeEndSessionUrl(url: string | null | undefined): string | undefined {
  if (!url) return undefined;
  if (url.startsWith('/') && !url.startsWith('//')) return url;
  try {
    const { protocol } = new URL(url);
    return protocol === 'https:' || protocol === 'http:' ? url : undefined;
  } catch {
    return undefined;
  }
}

export function isSessionExpiry(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}
