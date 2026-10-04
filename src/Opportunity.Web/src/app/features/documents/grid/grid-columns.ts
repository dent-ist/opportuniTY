import type { FieldResource, SearchHit } from '../../../core/api/generated/models';

/** How a cell value is shown (and aligned). */
export type ColumnFormat = 'text' | 'date' | 'size' | 'number';

/** A View column of the document list. Control Number and the family indicator are fixed, not View columns. */
export interface GridColumn {
  /** Query name of the field (`GET …/fields`), stable across renames. */
  readonly queryName: string;
  readonly label: string;
  readonly format: ColumnFormat;
  /** The `SearchHit` attribute that carries the value. */
  readonly value: (hit: SearchHit) => string | number | null;
  /** Sort key accepted by `POST …/searches`; null when the column cannot be sorted. */
  readonly sortField: string | null;
  /** CSS track size in the row grid. */
  readonly width: string;
}

interface ColumnSource {
  readonly label: string;
  readonly format: ColumnFormat;
  readonly value: (hit: SearchHit) => string | number | null;
  readonly sortField: string | null;
  readonly width: string;
}

/**
 * Fields a search page carries (`SearchHit`, ADR-015 D8.5) keyed by query name, with the API sort key
 * (`SearchSortFields`). Further columns appear when the hit carries more of the projection (E07-T02).
 */
const SOURCES: Readonly<Record<string, ColumnSource>> = {
  controlnumber: {
    label: 'Control Number',
    format: 'text',
    value: (h) => h.controlNumber,
    sortField: 'controlNumber',
    width: '10rem',
  },
  date: {
    label: 'Document Date',
    format: 'date',
    value: (h) => (h.documentDate as string | null) ?? null,
    sortField: 'documentDate',
    width: '13rem',
  },
  filename: {
    label: 'File Name',
    format: 'text',
    value: (h) => h.fileName,
    sortField: 'fileName',
    width: 'minmax(12rem, 2fr)',
  },
  filetype: {
    label: 'File Type',
    format: 'text',
    value: (h) => h.fileType,
    sortField: 'fileType',
    width: 'minmax(8rem, 1fr)',
  },
  extension: {
    label: 'File Extension',
    format: 'text',
    value: (h) => h.fileExtension,
    sortField: 'fileExtension',
    width: '8.5rem',
  },
  filesize: {
    label: 'File Size',
    format: 'size',
    value: (h) => (h.fileSize === null ? null : Number(h.fileSize)),
    sortField: 'fileSize',
    width: '6.5rem',
  },
  pagecount: {
    label: 'Page Count',
    format: 'number',
    value: (h) => (h.pageCount === null ? null : Number(h.pageCount)),
    sortField: 'pageCount',
    width: '7rem',
  },
  mimetype: {
    label: 'MIME Type',
    format: 'text',
    value: (h) => h.mimeType,
    sortField: null,
    width: 'minmax(8rem, 1fr)',
  },
};

/** Default View, in order (familiarity guide §3.1: Control Number pinned first, then the View columns). */
const DEFAULT_VIEW = ['date', 'filename', 'filetype', 'extension', 'filesize', 'pagecount'];

export const CONTROL_NUMBER = column('controlnumber', SOURCES['controlnumber'], true);

/**
 * The default View's columns from the workspace's field catalogue (`GET …/fields`, E07-T02): a column is shown
 * when its field exists and is not hidden, uses the field's display name, and is sortable only when the field
 * reports the `sortable` capability and the search API has a sort key for it. Without a catalogue (not loaded
 * or unavailable) the structural defaults apply.
 */
export function defaultColumns(fields: readonly FieldResource[] | null): {
  controlNumber: GridColumn;
  view: GridColumn[];
} {
  if (!fields) {
    return {
      controlNumber: CONTROL_NUMBER,
      view: DEFAULT_VIEW.map((q) => column(q, SOURCES[q], true)),
    };
  }
  const byName = new Map(fields.map((f) => [f.queryName.toLowerCase(), f]));
  const control = byName.get('controlnumber');
  return {
    controlNumber: control ? fromField('controlnumber', control) : CONTROL_NUMBER,
    view: DEFAULT_VIEW.flatMap((q) => {
      const field = byName.get(q);
      return field && !field.isHidden ? [fromField(q, field)] : [];
    }),
  };
}

function fromField(queryName: string, field: FieldResource): GridColumn {
  return {
    ...column(queryName, SOURCES[queryName], field.capabilities.sortable),
    label: field.displayName,
  };
}

function column(queryName: string, source: ColumnSource, sortable: boolean): GridColumn {
  return { queryName, ...source, sortField: sortable ? source.sortField : null };
}

/** Family/attachment marker (familiarity guide §3.1) with its text alternative. */
export interface FamilyMarker {
  readonly symbol: string;
  readonly label: string;
}

/**
 * `└A` for an attachment (the hit has a parent), `P` for the top-level document of a family with attachments
 * (`isFamilyParent`). Standalone documents show no marker; the duplicate ("D") marker needs a flag the search page does
 * not carry yet.
 */
export function familyMarker(hit: SearchHit): FamilyMarker | null {
  if (hit.parentDocumentId) return { symbol: '└A', label: 'Attachment' };
  return hit.isFamilyParent ? { symbol: 'P', label: 'Parent' } : null;
}
