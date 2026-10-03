import { HttpClient } from '@angular/common/http';
import { DOCUMENT, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
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
  /** POST (anti-forgery protected) → revokes the server session; responds 200 `{ redirectUrl }` or 204. */
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

/** The fields of `/api/v1/me` the shell needs; the generated client supplies the full type once published. */
export interface SessionPrincipal {
  id: string;
  displayName: string;
  [field: string]: unknown;
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

  async logout(): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<{ redirectUrl?: string } | null>(this.config.logoutUrl, null),
    ).catch((e: unknown) => {
      const error = toApiError(e);
      if (error.status === 401) return null;
      throw error;
    });
    this._principal.set(null);
    this._status.set('anonymous');
    this.document.location.assign(response?.redirectUrl ?? '/');
  }

  private currentPath(): string {
    const { pathname, search, hash } = this.document.location;
    return `${pathname}${search}${hash}`;
  }
}

export function isSessionExpiry(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}
