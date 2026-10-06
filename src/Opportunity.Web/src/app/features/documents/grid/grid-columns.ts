import type { FieldResource, SearchHit } from '../../../core/api/generated/models';

/** How a cell value is shown (and aligned). */
export type ColumnFormat = 'text' | 'date' | 'size' | 'number';

/** A View column of the document list. Control Number and the family indicator are fixed, not View columns. */
export interface GridColumn {
  /** Query name of the field (`GET …/fields`), stable across renames. */
  readonly queryName: string;
  readonly label: string;
  readonly format: ColumnFormat;
  /** The value of a hit: a `SearchHit` attribute, or the field's values in `hit.fields`. */
  readonly value: (hit: SearchHit) => string | number | null;
  /** Sort key accepted by `POST …/searches`; null when the column cannot be sorted. */
  readonly sortField: string | null;
  /** Why the column cannot be sorted (shown with the disabled sort control); null when it can. */
  readonly sortReason: string | null;
  /** Why the column has no filter (field capability metadata); null when it can be filtered. */
  readonly filterReason: string | null;
  /** CSS track size in the row grid. */
  readonly width: string;
  /** The width the user chose, in CSS pixels; null for the default. */
  readonly widthPx: number | null;
  /** Kept in view when the list scrolls sideways (pinned columns come first). */
  readonly pinned: boolean;
  /** The value comes from `hit.fields`: the search must ask for this field (`SearchRequest.fields`). */
  readonly fromFields: boolean;
}

/** A column of a View as stored (`GridViewColumn`). */
export interface ColumnSpec {
  readonly field: string;
  readonly width?: number | null;
  readonly pinned?: boolean;
}

/** One sort level (`SearchSortKey`): the API sort key and the direction. */
export interface SortSpec {
  readonly field: string;
  readonly direction: 'asc' | 'desc';
}

/** Sort levels a user can set (E16-T09); Control Number always breaks ties after them. */
export const MAX_SORT_LEVELS = 3;
export const MIN_COLUMN_WIDTH = 40;
export const MAX_COLUMN_WIDTH = 1200;

interface ColumnSource {
  readonly label: string;
  readonly format: ColumnFormat;
  readonly value: (hit: SearchHit) => string | number | null;
  readonly sortField: string | null;
  readonly width: string;
}

/**
 * Fields a search page carries as `SearchHit` attributes (ADR-015 D8.5) keyed by query name, with the API sort key
 * (`SearchSortFields`). Every other field comes in `hit.fields` when the search asks for it.
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
  familydate: {
    // "Date (Family)" (ADR-009 R25, E09-T03): sorting by it keeps families together, parent first.
    label: 'Family Date',
    format: 'date',
    value: (h) => (h.familyDate as string | null | undefined) ?? null,
    sortField: 'familyDate',
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
    sortField: 'mimeType',
    width: 'minmax(8rem, 1fr)',
  },
};

/** The workspace Default View, in order (familiarity guide §3.1: Control Number pinned first, then these). */
export const DEFAULT_VIEW: readonly ColumnSpec[] = [
  'date',
  'familydate',
  'filename',
  'filetype',
  'extension',
  'filesize',
  'pagecount',
].map((field) => ({ field }));

export const CONTROL_NUMBER = column('controlnumber', SOURCES['controlnumber'], true);

/** The Default View's columns (see `buildColumns`). */
export function defaultColumns(fields: readonly FieldResource[] | null): {
  controlNumber: GridColumn;
  view: GridColumn[];
} {
  return buildColumns(fields, DEFAULT_VIEW);
}

/**
 * The columns of a View from the workspace's field catalogue (`GET …/fields`, E07-T02): a column is shown when its
 * field exists (and, in the Default View, is not hidden), uses the field's display name, and is sortable only when the
 * field reports the `sortable` capability; otherwise it says why. Pinned columns come first. Without a catalogue (not
 * loaded or unavailable) only the structural columns the search page carries can be shown.
 */
export function buildColumns(
  fields: readonly FieldResource[] | null,
  specs: readonly ColumnSpec[],
): { controlNumber: GridColumn; view: GridColumn[] } {
  const isDefault = specs === DEFAULT_VIEW;
  const byName = new Map((fields ?? []).map((f) => [f.queryName.toLowerCase(), f]));
  const control = byName.get('controlnumber');
  const view = specs.flatMap((spec) => {
    const queryName = spec.field.toLowerCase();
    if (queryName === 'controlnumber') return [];
    let built: GridColumn | null;
    if (!fields) {
      built = SOURCES[queryName] ? column(queryName, SOURCES[queryName], true) : null;
    } else {
      const field = byName.get(queryName);
      built = field && !(isDefault && field.isHidden) ? fromField(field) : null;
    }
    return built ? [withSpec(built, spec)] : [];
  });
  return {
    controlNumber: control ? fromField(control) : CONTROL_NUMBER,
    view: [...view.filter((c) => c.pinned), ...view.filter((c) => !c.pinned)],
  };
}

/** The stored form of the shown columns. */
export function toSpecs(columns: readonly GridColumn[]): ColumnSpec[] {
  return columns.map((c) => ({
    field: c.queryName,
    ...(c.widthPx ? { width: c.widthPx } : {}),
    ...(c.pinned ? { pinned: true } : {}),
  }));
}

/** Field query names the search must return values for (`SearchRequest.fields`). */
export function requestedFields(columns: readonly GridColumn[]): string[] {
  return columns.filter((c) => c.fromFields).map((c) => c.queryName);
}

/** Group of the column chooser (familiarity guide §3.1: structural fields, metadata, coding). */
export type ColumnGroup = 'structural' | 'metadata' | 'coding';

/** A field the column chooser offers. */
export interface AvailableColumn {
  readonly queryName: string;
  readonly label: string;
  readonly group: ColumnGroup;
  /** Why it cannot be shown in the list (not searchable); null when it can. */
  readonly unavailableReason: string | null;
  readonly sortable: boolean;
  readonly sortReason: string | null;
  readonly filterReason: string | null;
}

/** Every field of the catalogue as the column chooser lists it, grouped and by name. Control Number is fixed. */
export function availableColumns(fields: readonly FieldResource[] | null): AvailableColumn[] {
  if (!fields) {
    return Object.entries(SOURCES)
      .filter(([q]) => q !== 'controlnumber')
      .map(([queryName, s]) => ({
        queryName,
        label: s.label,
        group: 'structural' as const,
        unavailableReason: null,
        sortable: !!s.sortField,
        sortReason: null,
        filterReason: null,
      }));
  }
  const order: Record<ColumnGroup, number> = { structural: 0, metadata: 1, coding: 2 };
  return fields
    .filter((f) => f.queryName.toLowerCase() !== 'controlnumber')
    .map((f) => {
      const built = fromField(f);
      return {
        queryName: f.queryName.toLowerCase(),
        label: f.displayName,
        group: groupOf(f),
        unavailableReason: searchable(f)
          ? null
          : 'Not searchable, so the document list cannot show its values.',
        sortable: built.sortField !== null,
        sortReason: built.sortReason,
        filterReason: built.filterReason,
      };
    })
    .sort((a, b) => order[a.group] - order[b.group] || a.label.localeCompare(b.label));
}

function groupOf(field: FieldResource): ColumnGroup {
  const storage = String(field.storage).toLowerCase();
  return storage === 'coding' ? 'coding' : storage === 'metadata' ? 'metadata' : 'structural';
}

function searchable(field: FieldResource): boolean {
  return Object.values(field.capabilities ?? {}).some((v) => v === true);
}

/** Why a field cannot be sorted, from its type and capabilities (ADR-004/ADR-007 R8). */
function sortReason(field: FieldResource): string {
  if (!searchable(field)) return 'Not searchable, so it cannot be sorted.';
  switch (field.type) {
    case 'singleChoice':
    case 'multiChoice':
      return 'Choice fields cannot be sorted: the search index keeps choices as a set, not in order.';
    case 'user':
      return 'User fields cannot be sorted.';
    default:
      return 'This field cannot be sorted in the search index.';
  }
}

function fromField(field: FieldResource): GridColumn {
  const queryName = field.queryName.toLowerCase();
  const sortable = !!field.capabilities?.sortable;
  const filterable = !!field.capabilities?.filterable;
  const source = SOURCES[queryName];
  const base = source
    ? column(queryName, source, sortable)
    : {
        queryName,
        label: field.displayName,
        format: formatOf(field),
        value: fieldValue(field),
        sortField: sortable ? queryName : null,
        sortReason: null,
        filterReason: null,
        width: widthOf(field),
        widthPx: null,
        pinned: false,
        fromFields: true,
      };
  return {
    ...base,
    label: field.displayName,
    sortField: sortable ? (source?.sortField ?? queryName) : null,
    sortReason: sortable ? null : sortReason(field),
    filterReason: filterable
      ? null
      : searchable(field)
        ? 'This field cannot be filtered in the search index.'
        : 'Not searchable, so it cannot be filtered.',
  };
}

function formatOf(field: FieldResource): ColumnFormat {
  switch (field.type) {
    case 'date':
      return 'date';
    case 'integer':
    case 'decimal':
      return 'number';
    default:
      return 'text';
  }
}

function widthOf(field: FieldResource): string {
  switch (field.type) {
    case 'date':
      return '13rem';
    case 'integer':
    case 'decimal':
      return '7rem';
    case 'boolean':
      return '6rem';
    default:
      return 'minmax(9rem, 1fr)';
  }
}

/** The display value of `hit.fields[queryName]`: choice IDs as names, booleans as Yes/No, several values joined. */
function fieldValue(field: FieldResource): (hit: SearchHit) => string | number | null {
  const queryName = field.queryName.toLowerCase();
  const choices = new Map((field.choices ?? []).map((c) => [String(c.choiceId), c.name]));
  return (hit) => {
    const values = (hit.fields as Record<string, string[]> | null | undefined)?.[queryName];
    if (!values?.length) return null;
    switch (field.type) {
      case 'singleChoice':
      case 'multiChoice':
        return values.map((v) => choices.get(v) ?? v).join('; ');
      case 'boolean':
        return values.map((v) => (v === 'true' ? 'Yes' : 'No')).join('; ');
      case 'integer':
      case 'decimal':
        return values.length === 1 ? Number(values[0]) : values.join('; ');
      case 'date':
        return values[0];
      default:
        return values.join('; ');
    }
  };
}

function withSpec(col: GridColumn, spec: ColumnSpec): GridColumn {
  const px =
    spec.width && Number.isFinite(spec.width)
      ? Math.round(Math.min(MAX_COLUMN_WIDTH, Math.max(MIN_COLUMN_WIDTH, spec.width)))
      : null;
  // Pinned columns have a fixed width, so the next pinned column knows where to stick.
  const width = px ? `${px}px` : spec.pinned ? `${minRem(col.width)}rem` : col.width;
  return { ...col, widthPx: px, width, pinned: !!spec.pinned };
}

function column(queryName: string, source: ColumnSource, sortable: boolean): GridColumn {
  return {
    queryName,
    ...source,
    sortField: sortable ? source.sortField : null,
    sortReason: sortable || !source.sortField ? null : 'This field cannot be sorted.',
    filterReason: null,
    widthPx: null,
    pinned: false,
    fromFields: false,
  };
}

/** The minimum of a CSS track size in rem (`10rem`, `240px`, `minmax(12rem, 2fr)`). */
export function minRem(track: string): number {
  const px = /^([\d.]+)px$/.exec(track);
  if (px) return parseFloat(px[1]) / 16;
  return parseFloat(/([\d.]+)rem/.exec(track)?.[1] ?? '6');
}

/** Family/attachment marker (familiarity guide §3.1) with its text alternative. */
export interface FamilyMarker {
  readonly symbol: string;
  readonly label: string;
}

/**
 * `└A` for an attachment (the hit has a parent), `P` for the top-level document of a family with attachments
 * (`isFamilyParent`). Standalone documents show no marker. The duplicate indicator ("D", a button to the duplicate
 * group) is rendered next to it from `duplicateGroupId` (family-groups.ts).
 */
export function familyMarker(hit: SearchHit): FamilyMarker | null {
  if (hit.parentDocumentId) return { symbol: '└A', label: 'Attachment' };
  return hit.isFamilyParent ? { symbol: 'P', label: 'Parent' } : null;
}
