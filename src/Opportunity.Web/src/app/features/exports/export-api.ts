import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import type {
  CreateExportRequest,
  CursorPageOfExportExclusionResource,
  CursorPageOfExportFileResource,
  CursorPageOfExportResource,
  CursorPageOfFieldResource,
  ExportExclusionResource,
  ExportFileResource,
  ExportResource,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

// Exports (E12-T01 API, E12-T08 UI) as a port: the history, one export with its settings and report, the exclusion
// list (Q-15), the delivered files, and New Export. Files and packages download through the protected-content gateway
// (`…/exports/{id}/files/{fileId}/content`, `…/exports/{id}/package`): plain links, so every download is a new request
// the API authorizes and audits again. Provided by the export pages, so it is workspace-scoped.

export interface Page<T> {
  readonly items: readonly T[];
  readonly nextCursor: string | null;
}

/** A field the DAT can carry (`GET …/fields`; only fields the caller may see are listed). */
export interface ExportableField {
  readonly fieldId: number;
  readonly name: string;
  readonly queryName: string;
  readonly isSystem: boolean;
}

@Injectable()
export abstract class ExportApi {
  abstract list(cursor?: string | null): Promise<Page<ExportResource>>;
  abstract get(exportId: string): Promise<ExportResource>;
  /** `POST …/exports` (202 with the export and its job); a 400 carries the validation errors by setting. */
  abstract create(request: CreateExportRequest, idempotencyKey: string): Promise<ExportResource>;
  abstract exclusions(
    exportId: string,
    cursor?: string | null,
  ): Promise<Page<ExportExclusionResource>>;
  /** Delivered files of a completed export (the creator's own exports, `Export.Download`). */
  abstract files(exportId: string, cursor?: string | null): Promise<Page<ExportFileResource>>;
  abstract fields(): Promise<readonly ExportableField[]>;
  abstract fileUrl(exportId: string, fileId: string): string;
  abstract packageUrl(exportId: string): string;
}

@Injectable()
export class HttpExportApi extends ExportApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  list(cursor?: string | null): Promise<Page<ExportResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfExportResource>(this.context.apiUrl('exports'), {
        params: cursor ? { cursor, limit: 50 } : { limit: 50 },
      }),
    );
  }

  get(exportId: string): Promise<ExportResource> {
    return firstValueFrom(this.http.get<ExportResource>(this.context.apiUrl('exports', exportId)));
  }

  create(request: CreateExportRequest, idempotencyKey: string): Promise<ExportResource> {
    return firstValueFrom(
      this.http.post<ExportResource>(this.context.apiUrl('exports'), request, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  exclusions(exportId: string, cursor?: string | null): Promise<Page<ExportExclusionResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfExportExclusionResource>(
        this.context.apiUrl('exports', exportId, 'exclusions'),
        { params: cursor ? { cursor, limit: 200 } : { limit: 200 } },
      ),
    );
  }

  files(exportId: string, cursor?: string | null): Promise<Page<ExportFileResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfExportFileResource>(
        this.context.apiUrl('exports', exportId, 'files'),
        { params: cursor ? { cursor, limit: 200 } : { limit: 200 } },
      ),
    );
  }

  fields(): Promise<readonly ExportableField[]> {
    return firstValueFrom(
      this.http.get<CursorPageOfFieldResource>(this.context.apiUrl('fields')).pipe(
        map((page) =>
          page.items
            .filter((f) => !f.isHidden)
            .map((f) => ({
              fieldId: Number(f.fieldId),
              name: f.displayName,
              queryName: f.queryName,
              isSystem: f.isSystem,
            })),
        ),
      ),
    );
  }

  fileUrl(exportId: string, fileId: string): string {
    return this.context.apiUrl('exports', exportId, 'files', fileId, 'content');
  }

  packageUrl(exportId: string): string {
    return this.context.apiUrl('exports', exportId, 'package');
  }
}
