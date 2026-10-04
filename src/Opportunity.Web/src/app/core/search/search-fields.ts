import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../api/generated/api-configuration';
import { listFields } from '../api/generated/fn/fields/list-fields';
import type { FieldResource } from '../api/generated/models';

/**
 * Search field catalogue for the query bar and the conditions builder: `GET /api/v1/workspaces/{workspaceId}/fields`
 * (ADR-007 R8, #186) with the choice names of choice fields (E04-T03). The server already omits fields the caller
 * may not see (E05-T06); the client additionally drops fields search cannot use (no capability at all).
 */
export type SearchFieldType =
  | 'text'
  | 'keyword'
  | 'integer'
  | 'decimal'
  | 'date'
  | 'dateTime'
  | 'boolean'
  | 'singleChoice'
  | 'multiChoice'
  | 'user';

export interface SearchField {
  /** Name used in query text (`queryName:value`), case-insensitive. */
  readonly queryName: string;
  readonly displayName: string;
  readonly type: SearchFieldType;
  /** Choice names for single/multiple choice fields, in admin order. */
  readonly choices?: readonly string[];
}

@Injectable({ providedIn: 'root', useFactory: () => inject(HttpSearchFieldSource) })
export abstract class SearchFieldSource {
  /** Fields the caller may search in the workspace. */
  abstract fields(workspaceId: string): Promise<readonly SearchField[]>;
}

@Injectable({ providedIn: 'root' })
export class HttpSearchFieldSource extends SearchFieldSource {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;

  async fields(workspaceId: string): Promise<readonly SearchField[]> {
    const response = await firstValueFrom(listFields(this.http, this.rootUrl, { workspaceId }));
    return response.body.items.filter(isSearchable).map(toSearchField);
  }
}

function isSearchable(field: FieldResource): boolean {
  return Object.values(field.capabilities).some(Boolean);
}

export function toSearchField(field: FieldResource): SearchField {
  const type: SearchFieldType =
    field.type === 'date' && field.datePrecision === 'dateTime' ? 'dateTime' : field.type;
  const choices = field.choices?.map((c) => c.name);
  return {
    queryName: field.queryName,
    displayName: field.displayName,
    type,
    ...(choices ? { choices } : {}),
  };
}

export const SEARCH_FIELD_TYPE_LABELS: Record<SearchFieldType, string> = {
  text: 'Text',
  keyword: 'Keyword',
  integer: 'Whole number',
  decimal: 'Decimal',
  date: 'Date',
  dateTime: 'Date and time',
  boolean: 'Yes/No',
  singleChoice: 'Single choice',
  multiChoice: 'Multiple choice',
  user: 'User',
};
