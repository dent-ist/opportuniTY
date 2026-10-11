import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { createListExport } from '../../../core/api/generated/fn/exports/create-list-export';
import { getListExport } from '../../../core/api/generated/fn/exports/get-list-export';
import type {
  CreateListExportRequest,
  ListExportResource,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

/**
 * Export List (CSV) backends as a port (#181): `POST …/list-exports` freezes the list and queues the job (202),
 * `GET …/list-exports/{id}` follows it, and the file comes from the protected-content gateway
 * (`…/list-exports/{id}/content`, a plain link so every download is authorized and audited again).
 */
@Injectable()
export abstract class ListExportApi {
  abstract create(
    request: CreateListExportRequest,
    idempotencyKey: string,
  ): Promise<ListExportResource>;
  abstract get(exportId: string): Promise<ListExportResource>;
  abstract downloadUrl(exportId: string): string;
}

/** HTTP adapter over the generated client. */
@Injectable()
export class HttpListExportApi extends ListExportApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  create(request: CreateListExportRequest, idempotencyKey: string): Promise<ListExportResource> {
    return firstValueFrom(
      createListExport(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        'Idempotency-Key': idempotencyKey,
        body: request,
      }).pipe(map((r) => r.body)),
    );
  }

  get(exportId: string): Promise<ListExportResource> {
    return firstValueFrom(
      getListExport(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        exportId,
      }).pipe(map((r) => r.body)),
    );
  }

  downloadUrl(exportId: string): string {
    return this.context.apiUrl('list-exports', exportId, 'content');
  }
}
