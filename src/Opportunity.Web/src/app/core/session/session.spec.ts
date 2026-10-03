import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideOpportunityHttp } from '../api/http';
import { SessionService } from './session';

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
    backend.expectOne('/api/v1/me').flush({ id: 'u1', displayName: 'Alex Reviewer' });
    expect(await refresh).toBe('authenticated');
    expect(session.principal()?.displayName).toBe('Alex Reviewer');
  });

  it('is anonymous before sign-in and expired after a lost session', async () => {
    let refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await refresh).toBe('anonymous');

    refresh = session.refresh();
    backend.expectOne('/api/v1/me').flush({ id: 'u1', displayName: 'A' });
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

  it('logs out through the BFF and follows its redirect', async () => {
    const logout = session.logout();
    backend.expectOne({ method: 'POST', url: '/bff/logout' }).flush({ redirectUrl: '/signed-out' });
    await logout;
    expect(session.status()).toBe('anonymous');
    expect(assign).toHaveBeenCalledWith('/signed-out');
  });
});
