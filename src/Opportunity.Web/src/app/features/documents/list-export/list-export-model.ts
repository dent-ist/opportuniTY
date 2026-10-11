import { InjectionToken } from '@angular/core';
import type {
  CreateListExportRequest,
  FieldResource,
  ListExportResource,
} from '../../../core/api/generated/models';
import { AllResultsSelection, MAX_SELECTED_IDS, SelectionTarget } from '../grid/selection';
import { DEFAULT_LOCALE, formatNumber, formatterFor } from '../../../core/i18n';

/** How often the dialog asks for the job's progress (a token so tests can poll fast). */
export const LIST_EXPORT_POLL_MS = new InjectionToken<number>('LIST_EXPORT_POLL_MS', {
  factory: () => 1_000,
});

/** A column of the export: the view column's field and its name. */
export interface ExportColumn {
  readonly fieldId: number;
  readonly label: string;
}

/** Which rows go to the file: the checked rows, or every result of the list. */
export type RowScope = 'selection' | 'list';

/**
 * The view's columns as fields of the catalogue, in order. Control Number is always first in the file and is not
 * listed again; a column whose field is unknown (catalogue not loaded) is left out.
 */
export function exportColumns(
  fields: readonly FieldResource[] | null,
  viewQueryNames: readonly string[],
): ExportColumn[] {
  const byName = new Map((fields ?? []).map((f) => [f.queryName.toLowerCase(), f]));
  return viewQueryNames.flatMap((name) => {
    const field = byName.get(name.toLowerCase());
    return field && field.queryName.toLowerCase() !== 'controlnumber'
      ? [{ fieldId: Number(field.fieldId), label: field.displayName }]
      : [];
  });
}

/** The request for the chosen rows: checked rows as IDs, the whole list as its query (never as IDs). */
export function toRequest(
  rows: SelectionTarget,
  columns: readonly ExportColumn[],
): CreateListExportRequest {
  const fieldIds = columns.map((c) => c.fieldId);
  return rows.kind === 'all'
    ? { fieldIds, query: rows.query, ...(rows.expand ? { expand: rows.expand } : {}) }
    : { fieldIds, documentIds: [...rows.documentIds] };
}

/** Why the chosen rows cannot be exported as they are, or null. */
export function rowsProblem(
  rows: SelectionTarget | null,
  locale: string = DEFAULT_LOCALE,
): string | null {
  if (!rows || (rows.kind === 'documents' && rows.documentIds.length === 0)) {
    return 'There are no documents to export: run a search first.';
  }
  if (rows.kind === 'documents' && rows.documentIds.length > MAX_SELECTED_IDS) {
    return `More than ${formatNumber(MAX_SELECTED_IDS, locale)} documents are checked one by one. Export all results of the list instead, after narrowing it with a filter if needed.`;
  }
  return null;
}

/** The rows the scope names: the selection, or the whole list when nothing (or the whole list) is selected. */
export function rowsFor(
  scope: RowScope,
  selection: SelectionTarget | null,
  list: AllResultsSelection | null,
): SelectionTarget | null {
  return scope === 'selection' ? selection : list;
}

export function isActive(e: ListExportResource): boolean {
  return e.status === 'queued' || e.status === 'running';
}

/** One line on the job's state, for the dialog and its live region. */
export function progressText(e: ListExportResource, locale: string): string {
  const n = (v: unknown) => formatNumber(Number(v), locale);
  switch (e.status) {
    case 'queued':
      return e.documentCount === null
        ? 'Freezing the list…'
        : `Queued: ${n(e.documentCount)} ${Number(e.documentCount) === 1 ? 'document' : 'documents'} frozen.`;
    case 'running':
      return `Writing ${n(e.documentCount ?? 0)} rows…`;
    case 'completed': {
      const rows = Number(e.rowCount ?? 0);
      const left = Number(e.documentsExcluded ?? 0);
      const base = `Ready: ${n(rows)} ${rows === 1 ? 'row' : 'rows'}`;
      return left > 0
        ? `${base}. ${n(left)} ${left === 1 ? 'document was' : 'documents were'} left out because you can no longer access ${left === 1 ? 'it' : 'them'}.`
        : `${base}.`;
    }
    default:
      return `The export failed: ${e.statusReason ?? 'try again'}`;
  }
}

/** "12.4 KB" for the download link. */
export function sizeText(bytes: unknown, locale: string): string {
  return formatterFor(locale).bytes(Number(bytes ?? 0));
}
