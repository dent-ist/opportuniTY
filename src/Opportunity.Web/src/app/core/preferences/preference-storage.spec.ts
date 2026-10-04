import { TestBed } from '@angular/core/testing';
import { FakeApi, provideFakeApi } from '../api/fake-api.testing';
import { provideOpportunityHttp } from '../api/http';
import { PREFERENCES_URL, PreferenceStorage } from './preference-storage';
import { UiPreferences } from './ui-preferences';

describe('PreferenceStorage (user profile, E15-T03)', () => {
  let api: FakeApi;
  let server: Record<string, unknown>;

  function setup(): PreferenceStorage {
    api = new FakeApi()
      .on('GET', PREFERENCES_URL, () => ({ body: { values: server } }))
      .on('PUT', `${PREFERENCES_URL}/ui`, { status: 204 })
      .on('PUT', `${PREFERENCES_URL}/shortcuts`, { status: 204 })
      .on('DELETE', `${PREFERENCES_URL}/shortcuts`, { status: 204 });
    TestBed.configureTestingModule({
      providers: [...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    return TestBed.inject(PreferenceStorage);
  }

  beforeEach(() => {
    localStorage.clear();
    server = {};
  });

  it('saves profile keys to the server and keeps browser-only keys local', async () => {
    const storage = setup();
    storage.write('ui', { theme: 'dark' });
    storage.write('recentWorkspaces.user-1', ['ws-1']);
    await storage.flush();
    const puts = api.requests.filter((r) => r.method === 'PUT');
    expect(puts.map((r) => [r.url, r.body])).toEqual([
      [`${PREFERENCES_URL}/ui`, { theme: 'dark' }],
    ]);
    expect(JSON.parse(localStorage.getItem('opp.pref.recentWorkspaces.user-1')!)).toEqual(['ws-1']);

    storage.write('ui', { theme: 'dark' });
    await storage.flush();
    expect(api.requests.filter((r) => r.method === 'PUT')).toHaveLength(1);
  });

  it('removes a profile key on the server', async () => {
    const storage = setup();
    storage.write('shortcuts', { singleKey: false });
    storage.remove('shortcuts');
    await storage.flush();
    expect(api.urls('DELETE')).toEqual([`${PREFERENCES_URL}/shortcuts`]);
    expect(storage.read('shortcuts')).toBeUndefined();
  });

  it('takes the profile from the server after sign-in, which wins over this browser', async () => {
    localStorage.setItem('opp.pref.ui', JSON.stringify({ theme: 'light' }));
    localStorage.setItem('opp.pref.shortcuts', JSON.stringify({ singleKey: false }));
    localStorage.setItem('opp.pref.recentWorkspaces.user-1', JSON.stringify(['ws-2']));
    server = { ui: { theme: 'high-contrast', density: 'compact' } };
    const storage = setup();
    const prefs = TestBed.inject(UiPreferences);
    TestBed.tick();
    expect(prefs.theme()).toBe('light');

    await storage.load();
    TestBed.tick();
    expect(prefs.theme()).toBe('high-contrast');
    expect(prefs.density()).toBe('compact');
    expect(document.documentElement.getAttribute('data-theme')).toBe('high-contrast');
    expect(storage.read('recentWorkspaces.user-1')).toEqual(['ws-2']);
    // A profile key kept only in this browser so far moves into the profile; the server's keys are not
    // written back.
    expect(storage.read('shortcuts')).toEqual({ singleKey: false });
    await storage.flush();
    expect(
      api.requests.filter((r) => r.method !== 'GET').map((r) => [r.method, r.url, r.body]),
    ).toEqual([['PUT', `${PREFERENCES_URL}/shortcuts`, { singleKey: false }]]);
  });

  it('keeps a change made before the profile arrived', async () => {
    server = { ui: { theme: 'dark' } };
    const storage = setup();
    storage.write('ui', { theme: 'light' });
    await storage.load();
    expect(storage.read('ui')).toEqual({ theme: 'light' });
    await storage.flush();
    expect(api.urls('PUT')).toEqual([`${PREFERENCES_URL}/ui`]);
  });

  it('keeps working from the cache when the profile cannot be loaded', async () => {
    localStorage.setItem('opp.pref.ui', JSON.stringify({ theme: 'dark' }));
    const storage = setup();
    api.on('GET', PREFERENCES_URL, { status: 503, body: { status: 503 } });
    await storage.load();
    expect(storage.read('ui')).toEqual({ theme: 'dark' });
  });
});
