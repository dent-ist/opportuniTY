import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { listDocumentFolders } from '../../../core/api/generated/fn/search/list-document-folders';
import type {
  DocumentFolderPage,
  DocumentFolderResource,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

/** An imported folder (#179, Q-55) as the Folders tree shows it; counts are documents the caller may see. */
export interface DocumentFolder {
  readonly folderId: string;
  readonly parentFolderId: string | null;
  readonly name: string;
  /** Folder names joined by a backslash (`Mail\Inbox`). */
  readonly path: string;
  /** Filed directly in the folder. */
  readonly documentCount: number;
  /** In the folder and every folder below it. */
  readonly totalCount: number;
  readonly hasSubfolders: boolean;
}

/** One level of the tree: the children of a folder (or the top level), in name order. */
export interface FolderLevel {
  readonly items: readonly DocumentFolder[];
  readonly nextCursor: string | null;
  /** The index is still catching up, so counts may trail (≈). */
  readonly countsApproximate: boolean;
}

/** The read-only Folders tree (`GET …/document-folders`), one level at a time. */
export abstract class FolderApi {
  abstract level(parentId: string | null, cursor?: string | null): Promise<FolderLevel>;
}

export function toFolder(r: DocumentFolderResource): DocumentFolder {
  return {
    folderId: r.folderId,
    parentFolderId: r.parentFolderId ?? null,
    name: r.name,
    path: r.path,
    documentCount: Number(r.documentCount),
    totalCount: Number(r.totalCount),
    hasSubfolders: r.hasSubfolders,
  };
}

export function toLevel(page: DocumentFolderPage): FolderLevel {
  return {
    items: page.items.map(toFolder),
    nextCursor: page.nextCursor ?? null,
    countsApproximate: page.countsApproximate,
  };
}

@Injectable()
export class HttpFolderApi extends FolderApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;

  level(parentId: string | null, cursor: string | null = null): Promise<FolderLevel> {
    return firstValueFrom(
      listDocumentFolders(this.http, this.rootUrl, {
        workspaceId: this.workspaceId,
        ...(parentId ? { parentId } : {}),
        ...(cursor ? { cursor } : {}),
      }).pipe(map((r) => toLevel(r.body))),
    );
  }
}
