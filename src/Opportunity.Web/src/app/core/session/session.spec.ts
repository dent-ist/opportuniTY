import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideOpportunityHttp } from '../api/http';
import { SessionPrincipal, SessionService } from './session';

const alex: SessionPrincipal = {
  userId: 'u1',
  displayName: 'Alex Reviewer',
  email: null,
  groups: [],
  mfa: false,
  sessionExpiresAt: null,
};

describe('SessionService', () => {
  let session: SessionService;
  let backend: HttpTestingController;
  const assign = vi.fn();

  beforeEach(() => {
    assign.mockReset();
    const fakeDocument = {
      location: { assign, pathname: '/w/1/review', search: '?q=1', hash: '' },
      documentElement: document.documentElement,
    };
    TestBed.configureTestingModule({
      providers: [
        ...provideOpportunityHttp(),
        provideHttpClientTesting(),
        { provide: DOCUMENT, useValue: fakeDocument },
      ],
    });
    session = TestBed.inject(SessionService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('loads the principal from the BFF', async () => {
    const refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush(alex);
    expect(await refresh).toBe('authenticated');
    expect(session.principal()?.displayName).toBe('Alex Reviewer');
  });

  it('is anonymous before sign-in and expired after a lost session', async () => {
    let refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await refresh).toBe('anonymous');

    refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush(alex);
    await refresh;
    refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await refresh).toBe('expired');
  });

  it('starts login with a relative return URL only', () => {
    session.login();
    expect(assign).toHaveBeenLastCalledWith('/bff/login?returnUrl=%2Fw%2F1%2Freview%3Fq%3D1');
    session.login('//evil.example/');
    expect(assign).toHaveBeenLastCalledWith('/bff/login?returnUrl=%2F');
  });

  it('logs out through the BFF and then ends the IdP session', async () => {
    const logout = session.logout();
    backend
      .expectOne({ method: 'POST', url: '/bff/logout' })
      .flush({ endSessionUrl: 'https://idp.example.test/logout?id_token_hint=x' });
    await logout;
    expect(session.status()).toBe('anonymous');
    expect(assign).toHaveBeenCalledWith('https://idp.example.test/logout?id_token_hint=x');
  });

  it('lands on the sign-in page when there is no IdP end-session URL or it is unsafe', async () => {
    for (const endSessionUrl of [null, 'javascript:alert(1)', '//evil.example/']) {
      const logout = session.logout();
      backend.expectOne({ method: 'POST', url: '/bff/logout' }).flush({ endSessionUrl });
      await logout;
      expect(assign).toHaveBeenLastCalledWith('/sign-in?signedOut=true');
    }
  });
});
