import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { createAuditExport } from '../../core/api/generated/fn/audit/create-audit-export';
import { getAuditExport } from '../../core/api/generated/fn/audit/get-audit-export';
import { listAuditActions } from '../../core/api/generated/fn/audit/list-audit-actions';
import { listAuditEvents } from '../../core/api/generated/fn/audit/list-audit-events';
import { listAuditExports } from '../../core/api/generated/fn/audit/list-audit-exports';
import type {
  AuditActionResource,
  AuditEventResource,
  AuditExportResource,
  CreateAuditExportRequest,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { type AuditFilter, toQuery } from './audit-log-model';

export interface AuditEventsPage {
  readonly items: readonly AuditEventResource[];
  readonly nextCursor: string | null;
}

export interface AuditExportsPage {
  readonly items: readonly AuditExportResource[];
  readonly nextCursor: string | null;
}

/** Events per page of the viewer: the list is never read all at once (E14-T04). */
export const AUDIT_PAGE_SIZE = 100;

/**
 * The audit log over the API (E14-T04, `Audit.Read`):
 *
 * - `GET …/audit-events` with the filters, newest first, one cursor page at a time; every page read is audited;
 * - `GET …/audit-actions`: the closed taxonomy for the Category and Action filters;
 * - `POST …/audit-exports` (Idempotency-Key, 202 + job) and `GET …/audit-exports[/{id}]`: the caller's exports;
 *   the CSV is `…/audit-exports/{id}/content` through the protected-content gateway (audited per download).
 */
@Injectable()
export abstract class AuditLogApi {
  abstract events(filter: AuditFilter, cursor?: string | null): Promise<AuditEventsPage>;
  abstract actions(): Promise<readonly AuditActionResource[]>;
  /** `idempotencyKey` is kept per request, so a retried click does not start a second export. */
  abstract createExport(filter: AuditFilter, idempotencyKey: string): Promise<AuditExportResource>;
  abstract exports(cursor?: string | null): Promise<AuditExportsPage>;
  abstract export(exportId: string): Promise<AuditExportResource>;
  abstract downloadUrl(exportId: string): string;
}

@Injectable()
export class HttpAuditLogApi extends AuditLogApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  async events(filter: AuditFilter, cursor?: string | null): Promise<AuditEventsPage> {
    const response = await firstValueFrom(
      listAuditEvents(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        ...toQuery(filter),
        limit: AUDIT_PAGE_SIZE,
        ...(cursor ? { cursor } : {}),
      }),
    );
    return { items: response.body.items, nextCursor: response.body.nextCursor ?? null };
  }

  async actions(): Promise<readonly AuditActionResource[]> {
    const response = await firstValueFrom(
      listAuditActions(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    );
    return response.body;
  }

  async createExport(filter: AuditFilter, idempotencyKey: string): Promise<AuditExportResource> {
    const query = toQuery(filter);
    const body: CreateAuditExportRequest = {
      from: query.from ?? null,
      to: query.to ?? null,
      category: query.category ?? null,
      action: query.action ?? null,
      actorId: query.actorId ?? null,
      controlNumber: query.controlNumber ?? null,
      jobId: query.jobId ?? null,
      outcome: query.outcome ?? null,
      includeSearchText: query.includeSearchText ?? false,
    };
    const response = await firstValueFrom(
      createAuditExport(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        'Idempotency-Key': idempotencyKey,
        body,
      }),
    );
    return response.body;
  }

  async exports(cursor?: string | null): Promise<AuditExportsPage> {
    const response = await firstValueFrom(
      listAuditExports(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 20,
        ...(cursor ? { cursor } : {}),
      }),
    );
    return { items: response.body.items, nextCursor: response.body.nextCursor ?? null };
  }

  async export(exportId: string): Promise<AuditExportResource> {
    const response = await firstValueFrom(
      getAuditExport(this.http, this.rootUrl, { workspaceId: this.context.workspaceId, exportId }),
    );
    return response.body;
  }

  downloadUrl(exportId: string): string {
    return this.context.apiUrl('audit-exports', exportId, 'content');
  }
}
