import { TestBed } from '@angular/core/testing';
import { FakeApi, provideFakeApi } from '../api/fake-api.testing';
import { provideOpportunityHttp } from '../api/http';
import { SessionService } from '../session/session';
import { allowedSections, ADMIN_AREAS, PERMISSIONS, WORKSPACE_SECTIONS } from './sections';
import { WorkspaceDirectory } from './workspace-api';
import { RecentWorkspaces } from './workspace-context';

describe('workspace core', () => {
  let api: FakeApi;

  beforeEach(() => {
    localStorage.clear();
    api = new FakeApi();
    TestBed.configureTestingModule({
      providers: [...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
  });

  it('loads a workspace once per navigation and retries after a failure', async () => {
    const directory = TestBed.inject(WorkspaceDirectory);
    api.on('GET', '/api/v1/workspaces/a%2Fb', { status: 503, body: { status: 503 } });
    await expect(directory.get('a/b')).rejects.toMatchObject({ status: 503 });
    api.on('GET', '/api/v1/workspaces/a%2Fb', {
      body: { workspaceId: 'a/b', name: 'A', permissions: [] },
    });
    await Promise.all([directory.get('a/b'), directory.get('a/b')]);
    expect(api.urls()).toEqual(['/api/v1/workspaces/a%2Fb', '/api/v1/workspaces/a%2Fb']);
  });

  it('pages the workspace list with a cursor and remembers names for the switcher', async () => {
    const directory = TestBed.inject(WorkspaceDirectory);
    api.on('GET', '/api/v1/workspaces', (req) => ({
      body: req.params.has('cursor')
        ? { items: [{ workspaceId: 'w2', name: 'Two' }], nextCursor: null }
        : { items: [{ workspaceId: 'w1', name: 'One' }], nextCursor: 'c1' },
    }));
    const first = await directory.list();
    await directory.list(first.nextCursor);
    expect(api.urls()).toEqual([
      '/api/v1/workspaces?limit=100',
      '/api/v1/workspaces?limit=100&cursor=c1',
    ]);
    expect([...directory.known().keys()]).toEqual(['w1', 'w2']);
  });

  it('keeps the five most recent workspace ids per user', async () => {
    api.on('GET', '/api/v1/me', { body: { userId: 'u1', displayName: 'U', groups: [] } });
    await TestBed.inject(SessionService).refresh();
    const recent = TestBed.inject(RecentWorkspaces);
    for (const id of ['w1', 'w2', 'w3', 'w4', 'w5', 'w6', 'w2']) recent.visit(id);
    expect(recent.ids()).toEqual(['w2', 'w6', 'w5', 'w4', 'w3']);
    expect(localStorage.getItem('opp.pref.recentWorkspaces.u1')).toBe(
      JSON.stringify(['w2', 'w6', 'w5', 'w4', 'w3']),
    );
  });

  it('filters sections and admin areas by permission, keeping the familiar order', () => {
    const sections = allowedSections(WORKSPACE_SECTIONS, [
      PERMISSIONS.jobView,
      PERMISSIONS.documentView,
    ]);
    expect(sections.map((s) => s.label)).toEqual(['Documents', 'Jobs']);
    expect(allowedSections(ADMIN_AREAS, [PERMISSIONS.auditRead]).map((a) => a.label)).toEqual([
      'Audit',
    ]);
    expect(WORKSPACE_SECTIONS.map((s) => s.label)).not.toContain('Search');
  });
});
