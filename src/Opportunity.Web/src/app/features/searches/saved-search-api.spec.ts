import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import {
  ActiveWorkspace,
  WORKSPACE_DATA,
  WorkspaceContext,
} from '../../core/workspace/workspace-context';
import { HttpSavedSearchApi } from './saved-search-api';

const WS = '/api/v1/workspaces/ws-1';

function resource(extra: object = {}) {
  return {
    savedSearchId: 'ss-1',
    name: 'Hot docs',
    folderId: null,
    owner: { userId: 'u-1', displayName: 'Alex' },
    scope: 'private',
    sharedWith: [],
    lastRunAt: null,
    lastHitCount: '12',
    lastHitRelation: 'eq',
    lastRunFreshness: null,
    modifiedAt: '2026-10-01T09:00:00Z',
    version: '7',
    query: 'custodian:smith',
    columns: null,
    sort: null,
    includeFamily: null,
    astVersion: '1',
    ...extra,
  };
}

describe('HttpSavedSearchApi (wave-9 saved-search contract)', () => {
  let api: FakeApi;
  let port: HttpSavedSearchApi;

  beforeEach(() => {
    api = new FakeApi()
      .on('GET', `${WS}/saved-searches`, (req) => ({
        body: {
          items: [resource()],
          nextCursor: req.params.get('cursor') ? null : 'c2',
        },
      }))
      .on('GET', `${WS}/saved-searches/ss-1`, { body: resource() })
      .on('PUT', `${WS}/saved-searches/ss-1`, { body: resource({ version: 8 }) })
      .on('PUT', `${WS}/saved-searches/ss-1/sharing`, {
        body: resource({
          scope: 'shared',
          sharedWith: [{ kind: 'group', id: 'Review Team', displayName: 'Review Team' }],
        }),
      })
      .on('POST', `${WS}/saved-searches/ss-1/clone`, {
        status: 201,
        body: resource({ savedSearchId: 'ss-2' }),
      })
      .on('PUT', `${WS}/saved-search-folders/f-1`, {
        body: { folderId: 'f-1', name: 'Renamed', parentFolderId: null, version: '3' },
      })
      .on('GET', `${WS}/members`, {
        body: {
          items: [
            { kind: 'user', userId: 'u-2', displayName: 'Jamie Lee', groupName: null },
            { kind: 'user', userId: 'u-2', displayName: 'Jamie Lee', groupName: null },
            { kind: 'group', userId: null, displayName: null, groupName: 'Review Team' },
          ],
          nextCursor: null,
        },
      });
    TestBed.configureTestingModule({
      providers: [
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: new Map([['workspaceId', 'ws-1']]),
              data: { [WORKSPACE_DATA]: { workspaceId: 'ws-1', permissions: [] } },
            },
          },
        },
        WorkspaceContext,
        HttpSavedSearchApi,
      ],
    });
    TestBed.inject(ActiveWorkspace);
    port = TestBed.inject(HttpSavedSearchApi);
  });

  it('pages through the list and normalises numbers', async () => {
    const all = await port.listAll();
    expect(all).toHaveLength(2);
    expect(all[0].lastHitCount).toBe(12);
    expect(all[0].version).toBe(7);
    expect(api.urls()).toEqual([
      `${WS}/saved-searches?limit=200`,
      `${WS}/saved-searches?limit=200&cursor=c2`,
    ]);
    await port.list({ folderId: 'f-1', q: 'hot' });
    expect(api.urls().at(-1)).toBe(`${WS}/saved-searches?limit=100&folderId=f-1&q=hot`);
  });

  it('sends If-Match with the version on writes, and sharing as kind and id only', async () => {
    const saved = await port.get('ss-1');
    expect(saved).toMatchObject({ columns: [], sort: [], includeFamily: false, astVersion: 1 });
    await port.update('ss-1', saved.version, { name: 'New', folderId: null, query: saved.query });
    const put = api.requests.find((r) => r.method === 'PUT')!;
    expect(put.headers.get('If-Match')).toBe('"7"');
    expect(put.body).toEqual({ name: 'New', folderId: null, query: 'custodian:smith' });

    const shared = await port.share('ss-1', [
      { kind: 'group', id: 'Review Team', displayName: 'Review Team' } as never,
    ]);
    expect(api.requests.at(-1)?.body).toEqual({
      sharedWith: [{ kind: 'group', id: 'Review Team' }],
    });
    expect(shared.scope).toBe('shared');

    await port.clone('ss-1', 'Copy of Hot docs', 'f-1');
    expect(api.requests.at(-1)?.body).toEqual({ name: 'Copy of Hot docs', folderId: 'f-1' });

    const folder = await port.updateFolder(
      { folderId: 'f-1', name: 'Old', parentFolderId: null, version: 2 },
      'Renamed',
      null,
    );
    expect(api.requests.at(-1)?.headers.get('If-Match')).toBe('"2"');
    expect(folder.version).toBe(3);
  });

  it('offers workspace users and groups once each as sharing candidates', async () => {
    expect(await port.shareCandidates()).toEqual([
      { kind: 'group', id: 'Review Team', displayName: 'Review Team' },
      { kind: 'user', id: 'u-2', displayName: 'Jamie Lee' },
    ]);
  });
});
