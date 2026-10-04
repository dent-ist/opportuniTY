import { ApiError } from '../../core/api/problem-details';
import type { SavedSearchFolder, SavedSearchSummary } from './saved-search-api';
import {
  actionsFor,
  flattenFolders,
  folderAndDescendants,
  folderOptions,
  folderPath,
  folderTree,
  freshnessLabel,
  frozenLabel,
  hitCountLabel,
  savedSearchErrorText,
  sharingLabel,
} from './saved-search-model';

const folder = (folderId: string, name: string, parentFolderId: string | null = null) =>
  ({ folderId, name, parentFolderId, version: 1 }) satisfies SavedSearchFolder;

const FOLDERS = [
  folder('b', 'Privilege'),
  folder('a', 'First pass'),
  folder('c', 'Hot documents', 'a'),
  folder('d', 'Second level', 'c'),
  folder('x', 'Orphan', 'missing'),
];

function summary(extra: Partial<SavedSearchSummary> = {}): SavedSearchSummary {
  return {
    savedSearchId: 'ss-1',
    name: 'Hot docs',
    folderId: null,
    owner: { userId: 'u-1', displayName: 'Alex Reviewer' },
    scope: 'private',
    sharedWith: [],
    lastRunAt: null,
    lastHitCount: null,
    lastHitRelation: null,
    lastRunFreshness: null,
    modifiedAt: '2026-10-01T09:00:00Z',
    version: 1,
    ...extra,
  };
}

describe('saved search rules (E16-T11)', () => {
  it('builds the folder tree sorted by name, with folders of a missing parent at the top', () => {
    const tree = folderTree(FOLDERS);
    expect(tree.map((n) => n.folder.name)).toEqual(['First pass', 'Orphan', 'Privilege']);
    expect(tree[0].children.map((n) => n.folder.name)).toEqual(['Hot documents']);
    expect(flattenFolders(tree).map((n) => [n.folder.folderId, n.depth])).toEqual([
      ['a', 0],
      ['c', 1],
      ['d', 2],
      ['x', 0],
      ['b', 0],
    ]);
  });

  it('keeps folders that only a parent cycle reaches', () => {
    const tree = folderTree([folder('p', 'P', 'q'), folder('q', 'Q', 'p')]);
    expect(
      flattenFolders(tree)
        .map((n) => n.folder.folderId)
        .sort(),
    ).toEqual(['p', 'q']);
  });

  it('names folder paths and the folders a folder cannot move into', () => {
    expect(folderPath('d', FOLDERS)).toBe('First pass / Hot documents / Second level');
    expect(folderPath(null, FOLDERS)).toBe('No folder');
    expect([...folderAndDescendants('a', FOLDERS)].sort()).toEqual(['a', 'c', 'd']);
    expect(folderOptions(FOLDERS, folderAndDescendants('c', FOLDERS))).toEqual([
      { value: '', label: 'No folder' },
      { value: 'a', label: 'First pass' },
      { value: 'x', label: 'Orphan' },
      { value: 'b', label: 'Privilege' },
    ]);
  });

  it('describes sharing, hit counts and their freshness', () => {
    expect(sharingLabel(summary())).toBe('Private');
    const shared = summary({
      scope: 'shared',
      sharedWith: [
        { kind: 'user', id: 'u-2', displayName: 'Jamie Lee' },
        { kind: 'group', id: 'Review Team', displayName: 'Review Team' },
      ],
    });
    expect(sharingLabel(shared)).toBe('Shared with Jamie Lee and Review Team');
    expect(
      sharingLabel({
        ...shared,
        sharedWith: [...shared.sharedWith, { kind: 'user', id: 'u-3', displayName: 'Sam' }],
      }),
    ).toBe('Shared with Jamie Lee and 2 others');

    expect(hitCountLabel(summary(), 'en-US')).toBe('—');
    const run = { lastHitCount: 1240, lastHitRelation: 'eq' as const };
    expect(hitCountLabel(summary(run), 'en-US')).toBe('1,240');
    expect(hitCountLabel(summary({ ...run, lastHitRelation: 'gte' }), 'en-US')).toBe('≥ 1,240');
    const catchingUp = summary({
      ...run,
      lastRunFreshness: { state: 'catchingUp', asOf: '2026-10-03T10:42:00Z' },
    });
    expect(hitCountLabel(catchingUp, 'en-US')).toBe('≈ 1,240');
    expect(freshnessLabel(catchingUp, 'en-US', 'UTC')).toBe('Updating as of 10:42 AM');
    expect(
      freshnessLabel(
        summary({ lastRunFreshness: { state: 'current', asOf: '2026-10-03T10:42:00Z' } }),
        'en-GB',
        'UTC',
      ),
    ).toBe('Current as of 10:42');
  });

  it('labels a frozen set with its count, time and who froze it', () => {
    const set = {
      snapshotId: 's-1',
      name: 'Mass Edit',
      purpose: 'bulkCoding',
      status: 'ready',
      documentCount: 1240,
      frozenAt: '2026-10-03T10:42:00Z',
      createdBy: 'u-1',
    };
    expect(frozenLabel(set, 'u-1', 'en-US', 'UTC')).toBe(
      'Frozen set (snapshot): 1,240 documents, frozen Oct 3, 2026, 10:42 AM by you',
    );
    expect(frozenLabel({ ...set, documentCount: null }, 'u-2', 'en-US', 'UTC')).toContain(
      'documents still being frozen, frozen Oct 3, 2026, 10:42 AM by another user',
    );
  });

  it('offers owners and admins the management actions, sharing only with SavedSearch.Share', () => {
    const can =
      (...granted: string[]) =>
      (p: string) =>
        granted.includes(p);
    const own = actionsFor(summary(), { userId: 'u-1', can: can('SavedSearch.Share') });
    expect(own).toEqual({
      run: true,
      copy: true,
      edit: true,
      delete: true,
      share: true,
      massEdit: false,
    });
    expect(actionsFor(summary(), { userId: 'u-1', can: can() }).share).toBe(false);

    const theirs = actionsFor(summary(), {
      userId: 'u-2',
      can: can('SavedSearch.Share', 'Coding.Bulk'),
    });
    expect(theirs).toEqual({
      run: true,
      copy: true,
      edit: false,
      delete: false,
      share: false,
      massEdit: true,
    });
    const admin = actionsFor(summary(), {
      userId: 'u-2',
      can: can('SavedSearch.Share', 'Workspace.ManageSecurity'),
    });
    expect(admin.edit && admin.delete && admin.share).toBe(true);
  });

  it('explains failed changes in plain words', () => {
    expect(
      savedSearchErrorText(new ApiError(409, { title: 'Conflict', code: 'FOLDER_NOT_EMPTY' })),
    ).toContain('Only an empty folder can be deleted');
    expect(savedSearchErrorText(new ApiError(404, { title: 'Not found' }))).toContain(
      'no longer shared with you',
    );
    expect(savedSearchErrorText(new ApiError(412, { title: 'Precondition failed' }))).toContain(
      'Someone else changed it',
    );
    expect(
      savedSearchErrorText(
        new ApiError(400, {
          title: 'Invalid',
          errors: { query: ['The field "Custodian" no longer exists.'] },
        }),
      ),
    ).toBe('The field "Custodian" no longer exists.');
  });
});
