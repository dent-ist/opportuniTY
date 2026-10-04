import {
  HttpBackend,
  HttpErrorResponse,
  HttpEvent,
  HttpHeaders,
  HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import { Provider } from '@angular/core';
import { Observable, defer, of, throwError } from 'rxjs';

export interface FakeResponse {
  status?: number;
  body?: unknown;
  headers?: Record<string, string>;
}

type Handler = FakeResponse | ((req: HttpRequest<unknown>) => FakeResponse);

/**
 * In-memory API for shell and feature tests: answers `METHOD url` (query string excluded) from registered
 * handlers, 404 problem details otherwise, and records every request that reached the network.
 */
export class FakeApi implements HttpBackend {
  readonly requests: HttpRequest<unknown>[] = [];
  private readonly handlers = new Map<string, Handler>();

  on(method: string, url: string, handler: Handler): this {
    this.handlers.set(`${method} ${url}`, handler);
    return this;
  }

  urls(method = 'GET'): string[] {
    return this.requests.filter((r) => r.method === method).map((r) => r.urlWithParams);
  }

  handle(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    return defer(() => {
      this.requests.push(req);
      const handler = this.handlers.get(`${req.method} ${req.url}`);
      const {
        status = 200,
        body = null,
        headers,
      } = !handler
        ? { status: 404, body: { title: 'Not found', status: 404 } }
        : typeof handler === 'function'
          ? handler(req)
          : handler;
      return status >= 400
        ? throwError(() => new HttpErrorResponse({ status, error: body, url: req.url }))
        : of(new HttpResponse({ status, body, url: req.url, headers: new HttpHeaders(headers) }));
    });
  }
}

/** Put after `provideOpportunityHttp()` so it replaces the real backend. */
export function provideFakeApi(api: FakeApi): Provider[] {
  return [{ provide: HttpBackend, useValue: api }];
}
