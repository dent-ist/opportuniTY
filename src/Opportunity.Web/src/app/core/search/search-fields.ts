import { Injectable, inject } from '@angular/core';

/**
 * Search field catalogue for the query bar and the conditions builder. The shape follows the planned
 * `GET /api/v1/workspaces/{workspaceId}/fields` resource (ADR-007 R8) plus the choice names of choice fields
 * (E04-T03). The API does not publish that route yet, so `SearchFieldSource` is an injectable seam: the
 * default serves the structural fields every workspace has (ADR-007 §3). Replace it with an HTTP source over
 * the generated client once the endpoint exists; custom fields and choices then appear without UI changes.
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

@Injectable({ providedIn: 'root', useFactory: () => inject(StructuralSearchFieldSource) })
export abstract class SearchFieldSource {
  /** Fields the caller may search in the workspace. */
  abstract fields(workspaceId: string): Promise<readonly SearchField[]>;
}

const STRUCTURAL: readonly SearchField[] = [
  { queryName: 'controlnumber', displayName: 'Control Number', type: 'keyword' },
  { queryName: 'begbates', displayName: 'Beg Bates', type: 'keyword' },
  { queryName: 'endbates', displayName: 'End Bates', type: 'keyword' },
  { queryName: 'date', displayName: 'Document Date', type: 'dateTime' },
  { queryName: 'familydate', displayName: 'Date (Family)', type: 'dateTime' },
  { queryName: 'datesent', displayName: 'Date Sent', type: 'dateTime' },
  { queryName: 'datereceived', displayName: 'Date Received', type: 'dateTime' },
  { queryName: 'datecreated', displayName: 'Date Created', type: 'dateTime' },
  { queryName: 'datelastmodified', displayName: 'Date Last Modified', type: 'dateTime' },
  { queryName: 'filename', displayName: 'File Name', type: 'text' },
  { queryName: 'extension', displayName: 'File Extension', type: 'keyword' },
  { queryName: 'filetype', displayName: 'File Type', type: 'keyword' },
  { queryName: 'mimetype', displayName: 'MIME Type', type: 'keyword' },
  { queryName: 'filesize', displayName: 'File Size', type: 'integer' },
  { queryName: 'pagecount', displayName: 'Page Count', type: 'integer' },
  { queryName: 'familyid', displayName: 'Family ID', type: 'keyword' },
  { queryName: 'familystatus', displayName: 'Family Status', type: 'keyword' },
  { queryName: 'duplicategroup', displayName: 'Duplicate Group', type: 'keyword' },
  { queryName: 'duplicateprimary', displayName: 'Primary Duplicate', type: 'boolean' },
  { queryName: 'threadid', displayName: 'Email Thread ID', type: 'keyword' },
  { queryName: 'md5', displayName: 'MD5 Hash', type: 'keyword' },
  { queryName: 'sha1', displayName: 'SHA-1 Hash', type: 'keyword' },
  { queryName: 'sha256', displayName: 'SHA-256 Hash', type: 'keyword' },
  { queryName: 'texttruncated', displayName: 'Text Truncated', type: 'boolean' },
  { queryName: 'textmissing', displayName: 'Text Missing', type: 'boolean' },
  { queryName: 'nativemissing', displayName: 'Native Missing', type: 'boolean' },
  { queryName: 'imagesincomplete', displayName: 'Images Incomplete', type: 'boolean' },
  { queryName: 'textlength', displayName: 'Text Length', type: 'integer' },
];

/** Interim source until the fields endpoint exists: the structural fields of ADR-007 §3 only. */
@Injectable({ providedIn: 'root' })
export class StructuralSearchFieldSource extends SearchFieldSource {
  fields(): Promise<readonly SearchField[]> {
    return Promise.resolve(STRUCTURAL);
  }
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
