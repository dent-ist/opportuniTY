import type { Route } from '@playwright/test';

/**
 * The read-only Folders tree (#179) for the e2e mock API, in the shapes of `GET …/document-folders?parentId=&cursor=`
 * and `GET …/document-folders/{id}`: Custodians › Smith (with Inbox and Sent) and Custodians › Lee, and a top-level
 * Loose Files folder. A search with `folder` lists the folder's documents (see mock-api.ts): the first N of the list.
 */

interface Folder {
  folderId: string;
  parentFolderId: string | null;
  name: string;
  path: string;
  depth: number;
  documentCount: number;
  totalCount: number;
  hasSubfolders: boolean;
}

function folder(
  folderId: string,
  parentFolderId: string | null,
  path: string,
  documentCount: number,
  totalCount: number,
): Folder {
  const names = path.split('\\');
  return {
    folderId,
    parentFolderId,
    name: names[names.length - 1],
    path,
    depth: names.length,
    documentCount,
    totalCount,
    hasSubfolders: totalCount > documentCount,
  };
}

export class FoldersMock {
  /** `parentId` of every level requested (`null` for the top level), in order. */
  readonly levels: (string | null)[] = [];
  private readonly folders: Folder[] = [
    folder('fld-custodians', null, 'Custodians', 0, 30),
    folder('fld-smith', 'fld-custodians', 'Custodians\\Smith', 4, 22),
    folder('fld-smith-inbox', 'fld-smith', 'Custodians\\Smith\\Inbox', 12, 12),
    folder('fld-smith-sent', 'fld-smith', 'Custodians\\Smith\\Sent', 6, 6),
    folder('fld-lee', 'fld-custodians', 'Custodians\\Lee', 8, 8),
    folder('fld-loose', null, 'Loose Files', 3, 3),
  ];

  /** Documents a search within the folder lists. */
  size(folderId: string, includeSubfolders: boolean): number | null {
    const f = this.folders.find((x) => x.folderId === folderId);
    return f ? (includeSubfolders ? f.totalCount : f.documentCount) : null;
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const level = /^\/api\/v1\/workspaces\/[^/]+\/document-folders$/.exec(path);
    if (level && method === 'GET') {
      const parentId = url.searchParams.get('parentId');
      this.levels.push(parentId);
      const parent = parentId ? this.folders.find((f) => f.folderId === parentId) : null;
      if (parentId && !parent) return notFound(route);
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          parent: parent ?? null,
          items: this.folders.filter((f) => f.parentFolderId === parentId),
          nextCursor: null,
          countsApproximate: false,
        }),
      });
    }
    const one = /^\/api\/v1\/workspaces\/[^/]+\/document-folders\/([^/]+)$/.exec(path);
    if (one && method === 'GET') {
      const f = this.folders.find((x) => x.folderId === one[1]);
      if (!f) return notFound(route);
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(f),
      });
    }
    return undefined;
  }
}

function notFound(route: Route): Promise<void> {
  return route.fulfill({
    status: 404,
    contentType: 'application/problem+json',
    body: JSON.stringify({ title: 'Not found', status: 404, detail: 'No such folder.' }),
  });
}
