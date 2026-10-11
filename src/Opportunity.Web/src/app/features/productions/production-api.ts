import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import type {
  CreateProductionRequest,
  CursorPageOfExportFileResource,
  CursorPageOfProductionDocumentResource,
  CursorPageOfProductionResource,
  ExportFileResource,
  FinalizeProductionRequest,
  JobResource,
  ProductionDocumentResource,
  ProductionQcExceptionPageResource,
  ProductionQcReportResource,
  ProductionResource,
  ProductionSpecification,
  ProductionVerificationResource,
  ProductionVolumeResource,
  RedactionSetList,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import type { Page } from '../exports/export-api';

// Productions (E12-T02…T07 API, E12-T08 UI) as a port: the list, one production with its ETag (every change sends
// If-Match), Bates allocation, the QC gate (run, exceptions, finalize with overrides), discard/void, the documents with
// their Bates numbers, volume runs and re-verification. Reports, volume files and packages download through the
// protected-content gateway as plain links, so each download is authorized and audited again. Provided by the
// production pages, so it is workspace-scoped and replaceable in tests.

/** A production as read, with the ETag a change must send back. */
export interface LoadedProduction {
  readonly production: ProductionResource;
  readonly etag: string;
}

@Injectable()
export abstract class ProductionApi {
  abstract list(cursor?: string | null, limit?: number): Promise<Page<ProductionResource>>;
  abstract get(productionId: string): Promise<LoadedProduction>;
  /** `POST …/productions`: a draft of a frozen set, or the next version of a finalized production. */
  abstract create(request: CreateProductionRequest): Promise<LoadedProduction>;
  /** `PUT …/productions/{id}` with If-Match; releases a Bates allocation. */
  abstract update(
    current: LoadedProduction,
    specification: ProductionSpecification,
    name: string,
  ): Promise<LoadedProduction>;
  abstract allocate(productionId: string, idempotencyKey: string): Promise<JobResource>;
  abstract runQc(productionId: string): Promise<ProductionQcReportResource>;
  /** The latest QC run, or null when none ran yet. */
  abstract qc(productionId: string): Promise<ProductionQcReportResource | null>;
  abstract qcExceptions(
    productionId: string,
    check: string,
    cursor?: string | null,
  ): Promise<ProductionQcExceptionPageResource>;
  /** 409 with the QC report in the problem's `qc` when a blocking check fails or warnings need acknowledging. */
  abstract finalize(
    current: LoadedProduction,
    request: FinalizeProductionRequest,
  ): Promise<LoadedProduction>;
  abstract discard(current: LoadedProduction): Promise<LoadedProduction>;
  abstract voidProduction(current: LoadedProduction, reason: string): Promise<LoadedProduction>;
  abstract documents(
    productionId: string,
    cursor?: string | null,
    limit?: number,
  ): Promise<Page<ProductionDocumentResource>>;
  abstract volumes(productionId: string): Promise<Page<ProductionVolumeResource>>;
  abstract startVolume(
    productionId: string,
    idempotencyKey: string,
  ): Promise<ProductionVolumeResource>;
  abstract volumeFiles(
    productionId: string,
    volumeId: string,
    cursor?: string | null,
  ): Promise<Page<ExportFileResource>>;
  abstract verify(productionId: string): Promise<ProductionVerificationResource>;
  abstract redactionSets(): Promise<readonly { readonly id: string; readonly name: string }[]>;
  abstract qcReportUrl(productionId: string, format: 'csv' | 'pdf'): string;
  abstract volumeFileUrl(productionId: string, volumeId: string, fileId: string): string;
  abstract volumePackageUrl(productionId: string, volumeId: string): string;
  abstract verificationReportUrl(productionId: string, volumeId: string): string;
}

@Injectable()
export class HttpProductionApi extends ProductionApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  private url(...segments: string[]): string {
    return this.context.apiUrl('productions', ...segments);
  }

  list(cursor?: string | null, limit = 50): Promise<Page<ProductionResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfProductionResource>(this.url(), {
        params: cursor ? { cursor, limit } : { limit },
      }),
    );
  }

  get(productionId: string): Promise<LoadedProduction> {
    return firstValueFrom(
      this.http
        .get<ProductionResource>(this.url(productionId), { observe: 'response' })
        .pipe(map(loaded)),
    );
  }

  create(request: CreateProductionRequest): Promise<LoadedProduction> {
    return firstValueFrom(
      this.http
        .post<ProductionResource>(this.url(), request, { observe: 'response' })
        .pipe(map(loaded)),
    );
  }

  update(
    current: LoadedProduction,
    specification: ProductionSpecification,
    name: string,
  ): Promise<LoadedProduction> {
    return firstValueFrom(
      this.http
        .put<ProductionResource>(
          this.url(current.production.productionId),
          { specification, name },
          { observe: 'response', headers: { 'If-Match': current.etag } },
        )
        .pipe(map(loaded)),
    );
  }

  allocate(productionId: string, idempotencyKey: string): Promise<JobResource> {
    return firstValueFrom(
      this.http.post<JobResource>(this.url(productionId, 'bates-allocation'), null, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  runQc(productionId: string): Promise<ProductionQcReportResource> {
    return firstValueFrom(
      this.http.post<ProductionQcReportResource>(this.url(productionId, 'qc'), null),
    );
  }

  async qc(productionId: string): Promise<ProductionQcReportResource | null> {
    try {
      return await firstValueFrom(
        this.http.get<ProductionQcReportResource>(this.url(productionId, 'qc')),
      );
    } catch (e) {
      if ((e as { status?: number }).status === 404) return null;
      throw e;
    }
  }

  qcExceptions(
    productionId: string,
    check: string,
    cursor?: string | null,
  ): Promise<ProductionQcExceptionPageResource> {
    return firstValueFrom(
      this.http.get<ProductionQcExceptionPageResource>(this.url(productionId, 'qc', 'exceptions'), {
        params: cursor ? { check, cursor, limit: 100 } : { check, limit: 100 },
      }),
    );
  }

  finalize(
    current: LoadedProduction,
    request: FinalizeProductionRequest,
  ): Promise<LoadedProduction> {
    return this.write(current, 'finalize', request);
  }

  discard(current: LoadedProduction): Promise<LoadedProduction> {
    return this.write(current, 'discard', null);
  }

  voidProduction(current: LoadedProduction, reason: string): Promise<LoadedProduction> {
    return this.write(current, 'void', { reason });
  }

  private write(current: LoadedProduction, action: string, body: unknown) {
    return firstValueFrom(
      this.http
        .post<ProductionResource>(this.url(current.production.productionId, action), body, {
          observe: 'response',
          headers: { 'If-Match': current.etag },
        })
        .pipe(map(loaded)),
    );
  }

  documents(
    productionId: string,
    cursor?: string | null,
    limit = 100,
  ): Promise<Page<ProductionDocumentResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfProductionDocumentResource>(this.url(productionId, 'documents'), {
        params: cursor ? { cursor, limit } : { limit },
      }),
    );
  }

  volumes(productionId: string): Promise<Page<ProductionVolumeResource>> {
    return firstValueFrom(
      this.http.get<Page<ProductionVolumeResource>>(this.url(productionId, 'volumes'), {
        params: { limit: 50 },
      }),
    );
  }

  startVolume(productionId: string, idempotencyKey: string): Promise<ProductionVolumeResource> {
    return firstValueFrom(
      this.http.post<ProductionVolumeResource>(this.url(productionId, 'volumes'), null, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  volumeFiles(
    productionId: string,
    volumeId: string,
    cursor?: string | null,
  ): Promise<Page<ExportFileResource>> {
    return firstValueFrom(
      this.http.get<CursorPageOfExportFileResource>(
        this.url(productionId, 'volumes', volumeId, 'files'),
        { params: cursor ? { cursor, limit: 200 } : { limit: 200 } },
      ),
    );
  }

  verify(productionId: string): Promise<ProductionVerificationResource> {
    return firstValueFrom(
      this.http.post<ProductionVerificationResource>(this.url(productionId, 'verification'), null),
    );
  }

  redactionSets(): Promise<readonly { readonly id: string; readonly name: string }[]> {
    return firstValueFrom(
      this.http
        .get<RedactionSetList>(this.context.apiUrl('redaction-sets'))
        .pipe(
          map((list) =>
            list.items
              .filter((s) => !s.retired)
              .map((s) => ({ id: s.redactionSetId, name: s.name })),
          ),
        ),
    );
  }

  qcReportUrl(productionId: string, format: 'csv' | 'pdf'): string {
    return `${this.url(productionId, 'qc', 'report')}?format=${format}`;
  }

  volumeFileUrl(productionId: string, volumeId: string, fileId: string): string {
    return this.url(productionId, 'volumes', volumeId, 'files', fileId, 'content');
  }

  volumePackageUrl(productionId: string, volumeId: string): string {
    return this.url(productionId, 'volumes', volumeId, 'package');
  }

  verificationReportUrl(productionId: string, volumeId: string): string {
    return this.url(productionId, 'volumes', volumeId, 'verification-report');
  }
}

function loaded(response: HttpResponse<ProductionResource>): LoadedProduction {
  return { production: response.body!, etag: response.headers.get('ETag') ?? '' };
}
