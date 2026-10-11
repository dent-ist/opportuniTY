import type {
  CreateExportRequest,
  ExportColumnResource,
  ExportResource,
  ExportResourceStatus,
  ExportSettingsResource,
  JobResourceStatus,
} from '../../core/api/generated/models';
import type { JobStatus } from '../../ui';
import { DELIMITER_PRESETS } from '../imports/import-model';
import type { ExportableField } from './export-api';
import { formatterFor } from '../../core/i18n';

/** Steps of New Export (familiarity guide §5.5), in order. */
export type ExportStepId = 'source' | 'volume' | 'files' | 'loadFile' | 'review';

export const EXPORT_STEPS: readonly { readonly id: ExportStepId; readonly label: string }[] = [
  { id: 'source', label: 'Source' },
  { id: 'volume', label: 'Volume & folders' },
  { id: 'files', label: 'Files' },
  { id: 'loadFile', label: 'Load file' },
  { id: 'review', label: 'Review & run' },
];

/** One DAT column as the field list edits it: a field, or a computed column, with its header. */
export interface DatColumn {
  /** `field:<id>` or `column:<name>`; unique in the list. */
  readonly key: string;
  readonly fieldId: number | null;
  readonly column: Exclude<ExportColumnResource, null> | null;
  /** What the column holds, in the user's words. */
  readonly source: string;
  readonly header: string;
}

/** Computed columns an export may carry (Bates and designation columns belong to productions). */
export const EXPORT_COLUMNS: readonly {
  readonly column: Exclude<ExportColumnResource, null>;
  readonly source: string;
  readonly header: string;
}[] = [
  { column: 'familyId', source: 'Family ID', header: 'FamilyID' },
  { column: 'parentId', source: 'Parent ID', header: 'ParentID' },
  { column: 'nativePath', source: 'Native file path', header: 'NativePath' },
  { column: 'textPath', source: 'Text file path', header: 'TextPath' },
];

/** The import presets (Q-51 vendor-neutral names), without Custom: exports write one of the presets. */
export const DELIMITER_OPTIONS = DELIMITER_PRESETS.filter((p) => p.value !== 'custom').map((p) => ({
  value: p.value as string,
  label: p.label as string,
}));

export const ENCODING_OPTIONS = [
  { value: 'utf-8', label: 'UTF-8 with BOM' },
  { value: 'utf-16le', label: 'UTF-16LE' },
  { value: 'windows-1252', label: 'Windows-1252 (Western)' },
];

export const TEXT_ENCODING_OPTIONS = [
  { value: 'utf-8', label: 'UTF-8' },
  { value: 'utf-16le', label: 'UTF-16LE' },
];

export const SEPARATOR_OPTIONS = [
  { value: '\\', label: 'Backslash  \\' },
  { value: '/', label: 'Forward slash  /' },
];

/** Everything New Export asks for besides the frozen set. */
export interface ExportForm {
  name: string;
  volumePrefix: string;
  volumeStart: string;
  volumePadding: string;
  maxFilesPerFolder: string;
  includeNatives: boolean;
  includeText: boolean;
  includeImages: boolean;
  textEncoding: string;
  pathSeparator: string;
  delimiters: string;
  encoding: string;
  columns: readonly DatColumn[];
}

export function defaultExportForm(fields: readonly ExportableField[]): ExportForm {
  const control = fields.find((f) => f.queryName === 'controlnumber');
  const columns: DatColumn[] = [];
  if (control) columns.push(fieldColumn(control, 'ControlNumber'));
  columns.push(computedColumn('familyId'), computedColumn('parentId'));
  return withPathColumns({
    name: '',
    volumePrefix: 'VOL',
    volumeStart: '1',
    volumePadding: '3',
    maxFilesPerFolder: '1000',
    includeNatives: true,
    includeText: true,
    includeImages: true,
    textEncoding: 'utf-8',
    pathSeparator: '\\',
    delimiters: 'concordance',
    encoding: 'utf-8',
    columns,
  });
}

export function fieldColumn(field: ExportableField, header = field.name): DatColumn {
  return {
    key: `field:${field.fieldId}`,
    fieldId: field.fieldId,
    column: null,
    source: field.name,
    header,
  };
}

export function computedColumn(column: Exclude<ExportColumnResource, null>, header?: string) {
  const def = EXPORT_COLUMNS.find((c) => c.column === column);
  return {
    key: `column:${column}`,
    fieldId: null,
    column,
    source: def?.source ?? column,
    header: header ?? def?.header ?? column,
  } satisfies DatColumn;
}

/**
 * The native and text path columns follow the Files step: added at the end when the files are included, removed when
 * they are not (the API refuses a path column without its files).
 */
export function withPathColumns(form: ExportForm): ExportForm {
  let columns = form.columns.filter(
    (c) =>
      !(c.column === 'nativePath' && !form.includeNatives) &&
      !(c.column === 'textPath' && !form.includeText),
  );
  if (form.includeNatives && !columns.some((c) => c.column === 'nativePath')) {
    columns = [...columns, computedColumn('nativePath')];
  }
  if (form.includeText && !columns.some((c) => c.column === 'textPath')) {
    columns = [...columns, computedColumn('textPath')];
  }
  return { ...form, columns };
}

export function moveColumn(
  columns: readonly DatColumn[],
  index: number,
  delta: -1 | 1,
): DatColumn[] {
  const target = index + delta;
  const copy = [...columns];
  if (target < 0 || target >= copy.length) return copy;
  [copy[index], copy[target]] = [copy[target], copy[index]];
  return copy;
}

function int(value: string): number | null {
  const n = Number(value.trim());
  return value.trim() && Number.isInteger(n) ? n : null;
}

/** `VOL001` for prefix VOL, start 1, padding 3 (guide §5.5). */
export function volumeName(prefix: string, start: number | null, padding: number | null): string {
  const p = prefix.trim() || 'VOL';
  return `${p}${String(start ?? 1).padStart(Math.min(Math.max(padding ?? 3, 1), 6), '0')}`;
}

/** A live example of where a native lands, with the chosen separator. */
export function examplePath(form: ExportForm): string {
  const sep = form.pathSeparator === '/' ? '/' : '\\';
  const volume = volumeName(form.volumePrefix, int(form.volumeStart), int(form.volumePadding));
  const [folder, file] = form.includeNatives
    ? ['NATIVES', 'ABC0000001.docx']
    : form.includeImages
      ? ['IMAGES', 'ABC0000001.tif']
      : form.includeText
        ? ['TEXT', 'ABC0000001.txt']
        : ['DATA', `${volume}.dat`];
  return folder === 'DATA'
    ? [volume, folder, file].join(sep)
    : [volume, folder, `${folder.slice(0, folder === 'IMAGES' ? 3 : 6)}0001`, file].join(sep);
}

/** Client-side checks of each step; the API validates everything again. */
export function stepProblems(id: ExportStepId, form: ExportForm, hasSet: boolean): string[] {
  const problems: string[] = [];
  if (id === 'source' && !hasSet) problems.push('Freeze the documents to export first.');
  if (id === 'volume') {
    if (!/^[A-Za-z0-9_-]{1,20}$/.test(form.volumePrefix.trim())) {
      problems.push("Volume prefix: use 1 to 20 letters, digits, '_' or '-'.");
    }
    const start = int(form.volumeStart);
    if (start === null || start < 1 || start > 999_999) {
      problems.push('Volume start: a whole number from 1 to 999,999.');
    }
    const padding = int(form.volumePadding);
    if (padding === null || padding < 1 || padding > 6) {
      problems.push('Volume padding: a whole number from 1 to 6.');
    }
    const max = int(form.maxFilesPerFolder);
    if (max === null || max < 1 || max > 100_000) {
      problems.push('Files per folder: a whole number from 1 to 100,000.');
    }
  }
  if (id === 'loadFile') {
    if (form.columns.length === 0) problems.push('List at least one DAT column.');
    const headers = form.columns.map((c) => c.header.trim().toLowerCase());
    if (headers.some((h) => !h)) problems.push('Every DAT column needs a header.');
    const dup = headers.find((h, i) => h && headers.indexOf(h) !== i);
    if (dup) problems.push(`The header "${dup}" is used twice.`);
  }
  return problems;
}

export function toCreateRequest(snapshotId: string, form: ExportForm): CreateExportRequest {
  return {
    snapshotId,
    ...(form.name.trim() ? { name: form.name.trim() } : {}),
    includeNatives: form.includeNatives,
    includeText: form.includeText,
    includeImages: form.includeImages,
    textEncoding: form.textEncoding,
    pathSeparator: form.pathSeparator,
    delimiters: form.delimiters,
    encoding: form.encoding,
    volume: {
      prefix: form.volumePrefix.trim(),
      start: int(form.volumeStart),
      padding: int(form.volumePadding),
      maxFilesPerFolder: int(form.maxFilesPerFolder),
    },
    fields: form.columns.map((c) =>
      c.fieldId !== null
        ? { fieldId: c.fieldId, header: c.header.trim() }
        : { column: c.column, header: c.header.trim() },
    ),
  };
}

/**
 * The same export again ("Re-run with same settings"): the stored settings of a past export for the same frozen set,
 * so the new volume has the identical membership (the access re-check still runs for the person re-running it).
 */
export function rerunRequest(previous: ExportResource): CreateExportRequest {
  const s: ExportSettingsResource = previous.settings;
  const vol = splitVolume(s.volumeName);
  return {
    snapshotId: previous.snapshotId,
    name: `${previous.name} (re-run)`,
    includeNatives: s.includeNatives,
    includeText: s.includeText,
    includeImages: s.includeImages,
    textEncoding: s.textEncoding,
    pathSeparator: s.pathSeparator,
    delimiters: s.delimiters,
    encoding: s.encoding,
    volume: { ...vol, maxFilesPerFolder: Number(s.maxFilesPerFolder) },
    fields: s.fields.map((f) =>
      f.fieldId !== null && f.fieldId !== undefined
        ? { fieldId: f.fieldId, header: f.header }
        : { column: f.column, header: f.header },
    ),
  };
}

/** `VOL001` → prefix VOL, start 1, padding 3. */
export function splitVolume(name: string): { prefix: string; start: number; padding: number } {
  const m = /^(.*?)(\d+)$/.exec(name);
  return m
    ? { prefix: m[1] || 'VOL', start: Number(m[2]), padding: m[2].length }
    : { prefix: name || 'VOL', start: 1, padding: 3 };
}

export const EXPORT_STATUS: Record<ExportResourceStatus, JobStatus> = {
  running: 'running',
  completed: 'succeeded',
  failed: 'failed',
  cancelled: 'cancelled',
};

export const JOB_STATUS: Record<JobResourceStatus, JobStatus> = {
  created: 'queued',
  preparing: 'queued',
  running: 'running',
  paused: 'running',
  cancelling: 'running',
  cancelled: 'cancelled',
  completed: 'succeeded',
  completedWithErrors: 'partial',
  failed: 'failed',
};

/** Human labels of the setting keys the API names in validation errors. */
export function settingLabel(key: string): string {
  const field = /^fields\[(\d+)\]$/.exec(key);
  if (field) return `DAT column ${Number(field[1]) + 1}`;
  return SETTING_LABELS[key] ?? key;
}

const SETTING_LABELS: Record<string, string> = {
  snapshotId: 'Frozen set',
  name: 'Name',
  fields: 'DAT columns',
  delimiters: 'Delimiters',
  encoding: 'DAT encoding',
  textEncoding: 'Text encoding',
  pathSeparator: 'Path separator',
  'volume.prefix': 'Volume prefix',
  'volume.start': 'Volume start',
  'volume.padding': 'Volume padding',
  'volume.maxFilesPerFolder': 'Files per folder',
};

export function formatBytes(bytes: number, locale: string): string {
  return formatterFor(locale).bytes(bytes);
}
