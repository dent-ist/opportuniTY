import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { DEFAULT_SESSION_CONFIG, SessionService } from '../session/session';
import { ActiveWorkspace, WORKSPACE_MISMATCH } from '../workspace/workspace-context';
import { provideOpportunityHttp } from './http';
import { ApiError } from './problem-details';

describe('API HTTP stack', () => {
  const xsrfCookie = 'opp-xsrf-test';
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ...provideOpportunityHttp({ ...DEFAULT_SESSION_CONFIG, xsrfCookieName: xsrfCookie }),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    TestBed.inject(ActiveWorkspace).enter({
      workspaceId: 'w1',
      name: 'Matter one',
      permissions: [],
    });
  });

  afterEach(() => {
    document.cookie = `${xsrfCookie}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`;
    backend.verify();
  });

  it('turns HTTP failures into ApiError with problem details', async () => {
    const call = firstValueFrom(http.get('/api/v1/workspaces/w1/documents/d1'));
    backend.expectOne('/api/v1/workspaces/w1/documents/d1').flush(
      {
        type: 'urn:opportunity:problem:not-found',
        title: 'Not found',
        status: 404,
        traceId: 'trace-1',
      },
      {
        status: 404,
        statusText: 'Not Found',
        headers: { 'Content-Type': 'application/problem+json' },
      },
    );
    const error = await call.catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('not-found');
    expect((error as ApiError).traceId).toBe('trace-1');
  });

  it('marks the session expired on a 401 from any API call', async () => {
    const session = TestBed.inject(SessionService);
    const call = firstValueFrom(http.get('/api/v1/workspaces'));
    backend
      .expectOne('/api/v1/workspaces')
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    await call.catch(() => undefined);
    expect(session.status()).toBe('expired');
  });

  it('sends the anti-forgery header on unsafe methods only', async () => {
    document.cookie = `${xsrfCookie}=token-123; path=/`;
    void firstValueFrom(http.post('/api/v1/workspaces/w1/searches', {}));
    void firstValueFrom(http.post('/bff/logout', null));
    void firstValueFrom(http.get('/api/v1/workspaces/w1/searches'));
    const post = backend.expectOne({ method: 'POST', url: '/api/v1/workspaces/w1/searches' });
    const logout = backend.expectOne({ method: 'POST', url: '/bff/logout' });
    const get = backend.expectOne({ method: 'GET' });
    expect(post.request.headers.get(DEFAULT_SESSION_CONFIG.xsrfHeaderName)).toBe('token-123');
    expect(logout.request.headers.get(DEFAULT_SESSION_CONFIG.xsrfHeaderName)).toBe('token-123');
    expect(get.request.headers.has(DEFAULT_SESSION_CONFIG.xsrfHeaderName)).toBe(false);
    post.flush({});
    logout.flush({});
    get.flush({});
  });

  it('reads the anti-forgery token from the BFF cookie by default', () => {
    expect(DEFAULT_SESSION_CONFIG.xsrfCookieName).toBe('__Host-opp-xsrf');
    expect(DEFAULT_SESSION_CONFIG.xsrfHeaderName).toBe('X-XSRF-TOKEN');
  });

  it('never sends a workspace-scoped call for a workspace other than the active one', async () => {
    const other = await firstValueFrom(http.get('/api/v1/workspaces/w2/documents')).catch(
      (e: unknown) => e,
    );
    expect((other as ApiError).code).toBe(WORKSPACE_MISMATCH);

    TestBed.inject(ActiveWorkspace).leave('w1');
    const outside = await firstValueFrom(http.get('/api/v1/workspaces/w1/documents')).catch(
      (e: unknown) => e,
    );
    expect((outside as ApiError).code).toBe(WORKSPACE_MISMATCH);

    // Installation-level workspace routes (list, single workspace) are not workspace-scoped calls.
    void firstValueFrom(http.get('/api/v1/workspaces/w2'));
    backend.expectOne('/api/v1/workspaces/w2').flush({});
    backend.expectNone(() => true);
  });
});
