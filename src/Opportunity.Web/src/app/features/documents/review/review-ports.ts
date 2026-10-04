import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { DocumentViewRecord } from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// The two backends Review mode talks to, as ports so the viewer (E16-T04) and coding pane (E16-T05) can be built
// against them while the document content API (E11-T01) and the coding API (E10-T01) land. The HTTP adapters
// follow ADR-019 (workspace-scoped routes, ETag/If-Match, problem details); the e2e mock API serves the same
// shapes. Both are provided by the Documents page, so they are workspace-scoped and replaceable in tests.

/**
 * Why content is fetched (ADR-013 §7.3, gateway `purpose`): `display` for the document on screen, `prefetch`
 * for the next one. Prefetch is audited as such and never counts as viewed.
 */
export type ContentPurpose = 'display' | 'prefetch';

/** The first chunk of a document's extracted text, as the gateway delivered it. */
export interface TextChunk {
  readonly documentId: string;
  readonly text: string;
  /** More text follows this chunk (the viewer loads it on demand, E16-T04). */
  readonly partial: boolean;
  /** The gateway's audit reference for this delivery (`X-Opportunity-Retrieval-Id`), sent with the view. */
  readonly retrievalId: string | null;
}

/** Bytes of extracted text in the first chunk. */
export const FIRST_CHUNK_BYTES = 64 * 1024;

@Injectable()
export abstract class DocumentContentApi {
  /** The first chunk of extracted text through the protected-content gateway; empty text when there is none. */
  abstract firstText(documentId: string, purpose: ContentPurpose): Promise<TextChunk>;
  /** Records that the viewer displayed the document (`Document.Viewed`); never called for a prefetch alone. */
  abstract recordView(documentId: string, retrievalId: string | null): Promise<void>;
}

/** `GET …/documents/{id}/text?purpose=` (range: the first chunk) and `POST …/documents/{id}/views`. */
@Injectable()
export class HttpDocumentContentApi extends DocumentContentApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async firstText(documentId: string, purpose: ContentPurpose): Promise<TextChunk> {
    let response: HttpResponse<Blob>;
    try {
      response = await firstValueFrom(
        this.http.get(this.context.apiUrl('documents', documentId, 'text'), {
          params: { purpose },
          headers: { Range: `bytes=0-${FIRST_CHUNK_BYTES - 1}` },
          observe: 'response',
          responseType: 'blob',
        }),
      );
    } catch (e) {
      // 416: the document has no extracted text (an empty object has no first byte).
      if ((e as { status?: number }).status === 416) {
        return { documentId, text: '', partial: false, retrievalId: null };
      }
      throw e;
    }
    const body = response.body ?? new Blob();
    const total = /\/(\d+)$/.exec(response.headers.get('Content-Range') ?? '')?.[1];
    return {
      documentId,
      text: decodeChunk(new Uint8Array(await body.arrayBuffer())),
      partial: response.status === 206 && total !== undefined && Number(total) > body.size,
      retrievalId: response.headers.get('X-Opportunity-Retrieval-Id'),
    };
  }

  async recordView(documentId: string, retrievalId: string | null): Promise<void> {
    const body: DocumentViewRecord = { retrievalId };
    await firstValueFrom(
      this.http.post(this.context.apiUrl('documents', documentId, 'views'), body),
    );
  }
}

/** UTF-8 text of a byte range, without the replacement character a cut multi-byte sequence leaves at the end. */
export function decodeChunk(bytes: Uint8Array): string {
  return new TextDecoder('utf-8').decode(bytes).replace(/�+$/, '');
}

/** A coding value as the API carries it (ADR-003 types: text, number, yes/no, choice names, user id). */
export type CodingValue = string | number | boolean | readonly string[] | null;

/** The current coding of a document (E10-T01). */
export interface DocumentCoding {
  readonly documentId: string;
  /** `DocumentVersion`, sent back as `If-Match` on save (ADR-019 §2.7). */
  readonly version: string;
  /** Values by field query name; fields without a value are absent. */
  readonly values: Readonly<Record<string, CodingValue>>;
  /** `pending` until the search index has the saved version ("Saved · indexing", E16-T05). */
  readonly indexState: 'searchable' | 'pending';
}

@Injectable()
export abstract class CodingApi {
  abstract get(documentId: string): Promise<DocumentCoding>;
  /** Saves changed values; rejects with 412 `version-conflict` when `version` is stale (never last-write-wins). */
  abstract save(
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
  ): Promise<DocumentCoding>;
}

interface CodingResource {
  documentId: string;
  documentVersion: string;
  values?: Record<string, CodingValue>;
  indexState?: 'searchable' | 'pending';
}

/** `GET` / `PUT …/documents/{id}/coding` with `If-Match` (E10-T01). */
@Injectable()
export class HttpCodingApi extends CodingApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async get(documentId: string): Promise<DocumentCoding> {
    const response = await firstValueFrom(
      this.http.get<CodingResource>(this.context.apiUrl('documents', documentId, 'coding'), {
        observe: 'response',
      }),
    );
    return toCoding(response);
  }

  async save(
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
  ): Promise<DocumentCoding> {
    const response = await firstValueFrom(
      this.http.put<CodingResource>(
        this.context.apiUrl('documents', documentId, 'coding'),
        { values },
        { observe: 'response', headers: { 'If-Match': `"${version}"` } },
      ),
    );
    return toCoding(response);
  }
}

function toCoding(response: HttpResponse<CodingResource>): DocumentCoding {
  const body = response.body!;
  const etag = response.headers.get('ETag')?.replace(/^W\//, '').replace(/"/g, '');
  return {
    documentId: body.documentId,
    version: etag || String(body.documentVersion),
    values: body.values ?? {},
    indexState: body.indexState ?? 'searchable',
  };
}
