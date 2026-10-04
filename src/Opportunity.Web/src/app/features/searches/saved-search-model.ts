import { ApiError, describeError } from '../../core/api/problem-details';
import type { FrozenSetSummary, SavedSearchFolder, SavedSearchSummary } from './saved-search-api';

// Pure rules of the Searches section (E16-T11): folder trees, labels and which actions a user has. Shared by the
// Saved Searches page and the Documents browser pane.

export const PERMISSION_SHARE = 'SavedSearch.Share';
/** Workspace Admin (the only role with it) may manage everyone's saved searches (wave-9 contract). */
export const PERMISSION_ADMIN = 'Workspace.ManageSecurity';
export const PERMISSION_BULK = 'Coding.Bulk';

/** The words that keep live and frozen sets apart everywhere (familiarity guide §1.2, ticket review E16-T11). */
export const LIVE_LABEL = 'Saved search (live)';
export const LIVE_EXPLANATION =
  'Runs against the current documents each time and shows only the documents the person running it may see.';
export const FILTERED_NOTICE = 'Results are filtered to the documents you may see.';

/** Section routes of `/w/:workspaceId/…` used by both pages. */
export const SAVED_SEARCH_PARAM = 'savedSearch';
/** `?then=massEdit`: after the saved search runs in Documents, its results open in Mass Edit. */
export const THEN_PARAM = 'then';

export interface FolderNode {
  readonly folder: SavedSearchFolder;
  readonly children: readonly FolderNode[];
  readonly depth: number;
}

const byName = (a: { name: string }, b: { name: string }) =>
  a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true });

/**
 * The folders as a tree, sorted by name. A folder whose parent is missing (not visible, or deleted meanwhile)
 * shows at the top level; parent cycles are cut.
 */
export function folderTree(folders: readonly SavedSearchFolder[]): FolderNode[] {
  const ids = new Set(folders.map((f) => f.folderId));
  const children = new Map<string | null, SavedSearchFolder[]>();
  for (const f of folders) {
    const parent = f.parentFolderId && ids.has(f.parentFolderId) ? f.parentFolderId : null;
    children.set(parent, [...(children.get(parent) ?? []), f]);
  }
  const placed = new Set<string>();
  const build = (parent: string | null, depth: number): FolderNode[] =>
    (children.get(parent) ?? [])
      .filter((f) => !placed.has(f.folderId) && placed.add(f.folderId))
      .sort(byName)
      .map((folder) => ({ folder, depth, children: build(folder.folderId, depth + 1) }));
  const roots = build(null, 0);
  // Folders only reachable through a cycle: show them at the top level rather than lose them.
  const orphans = folders
    .filter((f) => !placed.has(f.folderId))
    .sort(byName)
    .map((folder) => {
      placed.add(folder.folderId);
      return { folder, depth: 0, children: [] };
    });
  return [...roots, ...orphans];
}

/** Depth-first list of the tree (select options, keyboard order). */
export function flattenFolders(nodes: readonly FolderNode[]): FolderNode[] {
  return nodes.flatMap((n) => [n, ...flattenFolders(n.children)]);
}

/** "Team / First pass" for a folder id; "No folder" for null. */
export function folderPath(folderId: string | null, folders: readonly SavedSearchFolder[]): string {
  if (!folderId) return 'No folder';
  const byId = new Map(folders.map((f) => [f.folderId, f]));
  const names: string[] = [];
  const seen = new Set<string>();
  for (
    let f = byId.get(folderId);
    f && !seen.has(f.folderId);
    f = byId.get(f.parentFolderId ?? '')
  ) {
    seen.add(f.folderId);
    names.unshift(f.name);
  }
  return names.length ? names.join(' / ') : 'No folder';
}

/** Ids of a folder and every folder below it (a folder cannot move into these). */
export function folderAndDescendants(
  folderId: string,
  folders: readonly SavedSearchFolder[],
): Set<string> {
  const result = new Set([folderId]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const f of folders) {
      if (f.parentFolderId && result.has(f.parentFolderId) && !result.has(f.folderId)) {
        result.add(f.folderId);
        grew = true;
      }
    }
  }
  return result;
}

/** Folder choices for a select: "No folder" first, then the tree indented by depth. */
export function folderOptions(
  folders: readonly SavedSearchFolder[],
  exclude: ReadonlySet<string> = new Set(),
): { value: string; label: string }[] {
  return [
    { value: '', label: 'No folder' },
    ...flattenFolders(folderTree(folders))
      .filter((n) => !exclude.has(n.folder.folderId))
      .map((n) => ({ value: n.folder.folderId, label: `${'— '.repeat(n.depth)}${n.folder.name}` })),
  ];
}

/** "Private", or who it is shared with: "Shared with Jamie Lee and Review Team". */
export function sharingLabel(s: SavedSearchSummary): string {
  if (s.scope !== 'shared' || s.sharedWith.length === 0) return 'Private';
  const names = s.sharedWith.map((p) => p.displayName);
  if (names.length <= 2) return `Shared with ${names.join(' and ')}`;
  return `Shared with ${names[0]} and ${names.length - 1} others`;
}

/** Last hit count as the list shows it: exact, "≥ 10,000", or "≈ n" while the index was catching up (Q-10). */
export function hitCountLabel(s: SavedSearchSummary, locale: string): string {
  if (s.lastHitCount === null) return '—';
  const n = new Intl.NumberFormat(locale).format(s.lastHitCount);
  if (s.lastHitRelation === 'gte') return `≥ ${n}`;
  if (s.lastRunFreshness?.state === 'catchingUp') return `≈ ${n}`;
  return n;
}

/** Freshness stamp of the last hit count: "Current as of 10:42" or "Updating as of 10:42". */
export function freshnessLabel(s: SavedSearchSummary, locale: string, timeZone: string): string {
  const f = s.lastRunFreshness;
  if (!f) return '';
  const at = formatDate(f.asOf, locale, timeZone, { timeStyle: 'short' });
  return f.state === 'current' ? `Current as of ${at}` : `Updating as of ${at}`;
}

/** Frozen set label: "Frozen set (snapshot): 1,240 documents, frozen Oct 3, 2026, 10:42 AM by you". */
export function frozenLabel(
  s: FrozenSetSummary,
  me: string | null,
  locale: string,
  timeZone: string,
): string {
  const count =
    s.documentCount === null
      ? 'documents still being frozen'
      : `${new Intl.NumberFormat(locale).format(s.documentCount)} ${s.documentCount === 1 ? 'document' : 'documents'}`;
  const at = s.frozenAt ? `, frozen ${formatDate(s.frozenAt, locale, timeZone)}` : '';
  return `Frozen set (snapshot): ${count}${at} by ${s.createdBy === me ? 'you' : 'another user'}`;
}

export function formatDate(
  value: string | null | undefined,
  locale: string,
  timeZone: string,
  style: Intl.DateTimeFormatOptions = { dateStyle: 'medium', timeStyle: 'short' },
): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  try {
    return new Intl.DateTimeFormat(locale, { ...style, timeZone }).format(date);
  } catch {
    return new Intl.DateTimeFormat(locale, style).format(date);
  }
}

/** What the caller may do with one saved search. The API decides every call; this only hides dead ends. */
export interface SavedSearchActions {
  readonly run: boolean;
  readonly copy: boolean;
  /** Edit, rename and move. */
  readonly edit: boolean;
  readonly delete: boolean;
  readonly share: boolean;
  readonly massEdit: boolean;
}

export interface Caller {
  readonly userId: string | null;
  readonly can: (permission: string) => boolean;
}

export function actionsFor(s: SavedSearchSummary, caller: Caller): SavedSearchActions {
  const manage = s.owner.userId === caller.userId || caller.can(PERMISSION_ADMIN);
  return {
    run: true,
    copy: true,
    edit: manage,
    delete: manage,
    share: manage && caller.can(PERMISSION_SHARE),
    massEdit: caller.can(PERMISSION_BULK),
  };
}

/** Whether the person viewing it is not the owner (their results may differ: say so). */
export function sharedWithCaller(s: SavedSearchSummary, userId: string | null): boolean {
  return s.owner.userId !== userId;
}

/** Name for a copy: "Copy of Hot docs". */
export function copyName(name: string): string {
  return `Copy of ${name}`.slice(0, 200);
}

/** A failed change in the section's own words (the API's codes per the wave-9 contract and ADR-019). */
export function savedSearchErrorText(error: ApiError): string {
  const code = error.code.toLowerCase().replaceAll('_', '-');
  if (code === 'folder-not-empty')
    return 'Only an empty folder can be deleted. Move or delete its saved searches and folders first.';
  if (error.status === 404)
    return 'This saved search or folder is no longer available. It may have been deleted, or it is no longer shared with you.';
  if (code === 'version-conflict' || error.status === 412 || error.status === 409)
    return 'Someone else changed it in the meantime. The list has been refreshed; try again.';
  if (error.status === 403) return 'You do not have permission to do that.';
  if (error.status === 400) {
    const first = Object.values(error.problem.errors ?? {})[0]?.[0];
    return first ?? error.problem.detail ?? 'Some values need attention.';
  }
  return describeError(error).detail;
}
