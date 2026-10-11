import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { getDocumentCodingHistory } from '../../core/api/generated/fn/coding/get-document-coding-history';
import { listFields } from '../../core/api/generated/fn/fields/list-fields';
import type { CodingHistoryEntryResource, FieldResource } from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

export interface CodingHistoryPage {
  readonly items: readonly CodingHistoryEntryResource[];
  readonly nextCursor: string | null;
}

/** Changes per page of a document's history. */
export const HISTORY_PAGE_SIZE = 50;

/**
 * A document's coding history (E10-T05 API, E14-T04 UI): `GET …/documents/{id}/coding-history` (Document.View; fields
 * the reviewer may not see are never listed), newest first and cursor-paged, optionally for one field; and the
 * workspace's field catalogue (`GET …/fields`, read once) to name choices and offer the field filter.
 */
@Injectable()
export abstract class DocumentHistoryApi {
  abstract history(
    documentId: string,
    fieldId: string | null,
    cursor?: string | null,
  ): Promise<CodingHistoryPage>;
  abstract fields(): Promise<readonly FieldResource[]>;
}

@Injectable()
export class HttpDocumentHistoryApi extends DocumentHistoryApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);
  private catalog?: Promise<readonly FieldResource[]>;

  async history(
    documentId: string,
    fieldId: string | null,
    cursor?: string | null,
  ): Promise<CodingHistoryPage> {
    const response = await firstValueFrom(
      getDocumentCodingHistory(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        documentId,
        limit: HISTORY_PAGE_SIZE,
        ...(fieldId ? { fieldId } : {}),
        ...(cursor ? { cursor } : {}),
      }),
    );
    return { items: response.body.items, nextCursor: response.body.nextCursor ?? null };
  }

  fields(): Promise<readonly FieldResource[]> {
    this.catalog ??= firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    ).then(
      (r) => r.body.items,
      () => {
        this.catalog = undefined;
        return [];
      },
    );
    return this.catalog;
  }
}

/** A stored canonical value in the reviewer's words: choice names, Yes/No, lists joined; null when empty. */
export function valueText(value: unknown, field: FieldResource | undefined): string | null {
  if (value === null || value === undefined || value === '') return null;
  const one = (v: unknown): string => {
    if (typeof v === 'boolean') return v ? 'Yes' : 'No';
    const choice = field?.choices?.find((c) => String(c.choiceId) === String(v));
    if (choice) return choice.name;
    return typeof v === 'object' ? JSON.stringify(v) : String(v);
  };
  if (Array.isArray(value)) return value.length === 0 ? null : value.map(one).join(', ');
  return one(value);
}
