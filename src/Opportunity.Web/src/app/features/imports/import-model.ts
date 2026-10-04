import type {
  ColumnMapping,
  ColumnPreview,
  DelimiterInfo,
  ImportMode,
  JobResource,
  MappingTarget,
  TargetPreview,
} from '../../core/api/generated/models';
import type { JobStatus } from '../../ui';
import type {
  ImportProfileDraft,
  LoadFileOptions,
  OverlayOptions,
  PathOptions,
  SavedProfile,
} from './import-api';

// Pure rules of the import wizard (E08-T08, familiarity guide §5.1): steps, delimiter presets and glyphs, the request
// the settings make, column status wording and field type names. No Angular here, so every rule is unit-tested.

export type StepId = 'source' | 'format' | 'mapping' | 'overlay' | 'files' | 'validate';

export interface WizardStep {
  readonly id: StepId;
  readonly label: string;
}

const STEPS: readonly WizardStep[] = [
  { id: 'source', label: 'Source & mode' },
  { id: 'format', label: 'File format' },
  { id: 'mapping', label: 'Field mapping' },
  { id: 'overlay', label: 'Overlay settings' },
  { id: 'files', label: 'Natives, text & images' },
  { id: 'validate', label: 'Validate & run' },
];

/** The steps for a mode, in order: Overlay settings only when the import changes existing documents. */
export function stepsFor(mode: ImportMode): readonly WizardStep[] {
  return mode === 'append' ? STEPS.filter((s) => s.id !== 'overlay') : STEPS;
}

export const MODE_LABELS: Record<ImportMode, string> = {
  append: 'Append',
  overlay: 'Overlay',
  appendOverlay: 'Append/Overlay',
};

export const MODE_HINTS: Record<ImportMode, string> = {
  append: 'Load new documents only. A row whose key already exists is an error.',
  overlay: 'Update existing documents only. A row whose key does not exist is an error.',
  appendOverlay: 'Update documents that exist and load the rest as new documents.',
};

/** Delimiter presets with vendor-neutral names (Q-51); `custom` takes the characters below. */
export const DELIMITER_PRESETS = [
  { value: 'concordance', label: 'Concordance-style (DC4 / þ / ®)' },
  { value: 'concordance-pilcrow', label: 'Pilcrow / thorn (¶ / þ / ®)' },
  { value: 'csv', label: 'CSV (RFC 4180)' },
  { value: 'custom', label: 'Custom' },
] as const;

export type DelimiterPreset = (typeof DELIMITER_PRESETS)[number]['value'];

export const ENCODINGS = [
  { value: 'auto', label: 'Detect automatically' },
  { value: 'utf-8', label: 'UTF-8' },
  { value: 'utf-16le', label: 'UTF-16 LE (Unicode)' },
  { value: 'utf-16be', label: 'UTF-16 BE' },
  { value: 'windows-1252', label: 'Windows-1252 (ANSI)' },
] as const;

const CONTROL_NAMES: Record<number, string> = {
  9: 'TAB',
  10: 'LF',
  13: 'CR',
  20: 'DC4',
  29: 'GS',
  30: 'RS',
  31: 'US',
};

/** A delimiter as practitioners name it: glyph and decimal code, e.g. `þ (254)`, `DC4 (20)`. */
export function describeDelimiter(info: DelimiterInfo | null | undefined): string {
  if (!info) return 'None';
  const code = Number(info.decimal);
  const glyph =
    CONTROL_NAMES[code] ?? (code < 32 ? `U+${code.toString(16).padStart(4, '0')}` : info.char);
  return `${glyph} (${code})`;
}

/**
 * The delimiter preset a DAT most likely uses, from its leading bytes: a DC4 (20) column separator is the
 * Concordance-style default, a pilcrow (¶, 182 in any common encoding) the pilcrow variant, and a comma on the first
 * line CSV. Anything else keeps the default, which the preview then shows.
 */
export function detectPreset(bytes: Uint8Array): Exclude<DelimiterPreset, 'custom'> {
  const firstLine = bytes.subarray(0, Math.max(0, bytes.indexOf(10)) || bytes.length);
  if (firstLine.includes(20)) return 'concordance';
  if (firstLine.includes(0xb6)) return 'concordance-pilcrow';
  if (firstLine.includes(44)) return 'csv';
  return 'concordance';
}

/** `Matter A/VOL001`-style relative folder inside the import share; null when valid. */
export function volumeRootError(value: string): string | null {
  const v = value.trim();
  if (!v) return null;
  if (/^([a-z]:|[\\/])/i.test(v)) {
    return 'Enter a folder inside the import share, not an absolute path (for example matter-a/VOL001).';
  }
  if (v.split(/[\\/]/).some((part) => part === '..')) {
    return 'The folder cannot contain "..".';
  }
  return null;
}

/** Default import name: `<DAT file name> <yyyy-mm-dd>` (guide §5.1 step 1; the API uses the same default). */
export function defaultImportName(datName: string, today: Date): string {
  return `${datName} ${today.toISOString().slice(0, 10)}`;
}

export const DEFAULT_LOAD_FILE: LoadFileOptions = {
  delimiters: 'concordance',
  column: null,
  quote: null,
  newline: null,
  multiValue: null,
  datEncoding: 'auto',
  textEncoding: 'auto',
  firstLineContainsFieldNames: true,
};

export const DEFAULT_OVERLAY: OverlayOptions = {
  keyField: 'ControlNumber',
  blankValuesOverwrite: false,
  multiValue: 'replace',
  allowCodingFields: false,
};

export const DEFAULT_PATHS: PathOptions = {
  volumeRoot: null,
  stripPrefix: null,
  textInLoadFile: false,
  missingFiles: 'flag',
};

/** Everything the wizard's settings put into the request's `profile`. */
export interface WizardSettings {
  readonly mode: ImportMode;
  readonly loadFile: LoadFileOptions;
  readonly overlay: OverlayOptions;
  readonly paths: PathOptions;
  readonly imageMatchBy: 'controlNumber' | 'begBates';
  /** Mappings chosen in the wizard (or by a loaded profile), by column name. */
  readonly columns: Readonly<Record<string, ColumnMapping>>;
}

function blankToNull(value: string | null | undefined): string | null {
  const v = value?.trim();
  return v ? v : null;
}

/**
 * The request profile in the wave-8 contract's shape. A preset sends no characters (the preset defines them); custom
 * delimiters send only the characters entered. Overlay options go with every mode so a saved profile keeps them.
 */
export function buildProfile(
  s: WizardSettings,
  columns: readonly ColumnMapping[] = Object.values(s.columns),
): ImportProfileDraft {
  const custom = s.loadFile.delimiters === 'custom';
  return {
    loadFile: {
      ...s.loadFile,
      column: custom ? blankToNull(s.loadFile.column) : null,
      quote: custom ? blankToNull(s.loadFile.quote) : null,
      newline: custom ? blankToNull(s.loadFile.newline) : null,
      multiValue: custom ? blankToNull(s.loadFile.multiValue) : null,
    },
    mode: s.mode,
    overlay: s.overlay,
    paths: {
      ...s.paths,
      volumeRoot: blankToNull(s.paths.volumeRoot?.replace(/\\/g, '/')),
      stripPrefix: blankToNull(s.paths.stripPrefix),
    },
    images: { matchBy: s.imageMatchBy },
    unmappedColumns: 'ignore',
    columns,
  };
}

/** Settings a saved profile pre-fills; the wizard keeps its own values where the profile is silent. */
export function settingsFromProfile(
  profile: SavedProfile,
  current: WizardSettings,
): WizardSettings {
  const d = profile.definition;
  const lf = d.loadFile ?? {};
  const columns: Record<string, ColumnMapping> = {};
  for (const c of d.columns ?? []) if (c.column) columns[c.column] = c;
  return {
    mode: d.mode ?? current.mode,
    loadFile: {
      delimiters: lf.delimiters ?? current.loadFile.delimiters,
      column: lf.column ?? null,
      quote: lf.quote ?? null,
      newline: lf.newline ?? null,
      multiValue: lf.multiValue ?? null,
      datEncoding: lf.datEncoding ?? 'auto',
      textEncoding: lf.textEncoding ?? 'auto',
      firstLineContainsFieldNames: lf.firstLineContainsFieldNames ?? true,
    },
    overlay: { ...current.overlay, ...pickOverlay(d.overlay) },
    paths: {
      volumeRoot: d.paths?.volumeRoot ?? current.paths.volumeRoot,
      stripPrefix: d.paths?.stripPrefix ?? null,
      textInLoadFile: d.paths?.textInLoadFile ?? false,
      missingFiles: d.paths?.missingFiles === 'error' ? 'error' : 'flag',
    },
    imageMatchBy: d.images?.matchBy === 'begBates' ? 'begBates' : 'controlNumber',
    columns,
  };
}

function pickOverlay(o: Partial<OverlayOptions> | undefined): Partial<OverlayOptions> {
  if (!o) return {};
  const out: { -readonly [K in keyof OverlayOptions]?: OverlayOptions[K] } = {};
  if (typeof o.keyField === 'string') out.keyField = o.keyField;
  if (typeof o.blankValuesOverwrite === 'boolean')
    out.blankValuesOverwrite = o.blankValuesOverwrite;
  if (o.multiValue === 'merge' || o.multiValue === 'replace') out.multiValue = o.multiValue;
  if (typeof o.allowCodingFields === 'boolean') out.allowCodingFields = o.allowCodingFields;
  return out;
}

/** Field type display names (familiarity guide §1.1); the API keeps the ADR-003 names. */
const TYPE_LABELS: Record<string, string> = {
  text: 'Long Text',
  keyword: 'Short Text',
  integer: 'Whole Number',
  decimal: 'Decimal',
  date: 'Date',
  dateTime: 'Date and Time',
  boolean: 'Yes/No',
  singleChoice: 'Single Choice',
  multiChoice: 'Multiple Choice',
  user: 'User',
  path: 'File path',
};

export function typeLabel(type: string | null | undefined): string {
  if (!type) return '';
  return TYPE_LABELS[type] ?? type;
}

/** Stable key of a mapping target, the value of its option in the mapping grid. */
export function targetKey(t: MappingTarget): string {
  switch (t.kind) {
    case 'structural':
      return `s:${t.structural}`;
    case 'newField':
      return `n:${t.newField?.name ?? ''}`;
    default:
      return `f:${t.fieldId ?? t.fieldName ?? ''}`;
  }
}

/** Value of the "Do not import" option. */
export const IGNORE = 'ignore';
/** Value of a column that maps to several targets at once (e.g. BEGDOC → Control Number and Begin Bates). */
export const SEVERAL = 'several';

/** The mapping grid's selection for a column. */
export function selectionOf(column: ColumnPreview): string {
  if (column.status === 'ignored') return IGNORE;
  if (column.targets.length > 1) return SEVERAL;
  return column.targets[0] ? targetKey(column.targets[0].target) : '';
}

/** Is the column mapped to anything that will be imported? */
export function isMapped(column: ColumnPreview): boolean {
  return column.status === 'mapped' || column.status === 'mergedIntoDate';
}

function matchReason(t: TargetPreview): string {
  switch (t.matchedBy) {
    case 'alias':
      return t.alias ? `matched by alias ${t.alias}` : 'matched by alias';
    case 'exactName':
      return 'matched by name';
    case 'normalizedName':
      return 'matched by similar name';
    default:
      return 'chosen';
  }
}

/** The status column of the mapping grid in words; unmapped columns say so explicitly. */
export function columnStatus(column: ColumnPreview): string {
  switch (column.status) {
    case 'mapped': {
      const t = column.targets[0];
      const created = column.targets.some((x) => x.resolution === 'willCreate');
      const base = t ? `Mapped, ${matchReason(t)}` : 'Mapped';
      return created ? `${base} (new field)` : base;
    }
    case 'ignored':
      return 'Do not import';
    case 'storedAsNewTextField':
      return 'Not mapped: stored in a new Long Text field';
    case 'mergedIntoDate':
      return column.mergedInto
        ? `Time merged into ${column.mergedInto}`
        : 'Time merged into a date';
    default:
      return 'Not mapped: not imported';
  }
}

/** "Control Number + Begin Bates" for a column with several targets. */
export function targetLabels(column: ColumnPreview): string {
  return column.targets.map((t) => t.label).join(' + ');
}

/** Field types of the column's targets, in display names, without repeats. */
export function targetTypes(column: ColumnPreview): string {
  return [...new Set(column.targets.map((t) => typeLabel(t.type)))].join(', ');
}

/** The column that maps a structural target (Native Path, Extracted Text Path), or ''. */
export function columnFor(columns: readonly ColumnPreview[], structural: string): string {
  return (
    columns.find((c) => isMapped(c) && c.targets.some((t) => t.target.structural === structural))
      ?.column ?? ''
  );
}

/** Control Number is the key of every new document: an Append or Append/Overlay import must map it. */
export function mapsControlNumber(columns: readonly ColumnPreview[]): boolean {
  return columns.some(
    (c) =>
      isMapped(c) &&
      c.targets.some(
        (t) =>
          t.target.kind === 'field' &&
          (t.label === 'Control Number' || t.target.fieldName === 'Control Number'),
      ),
  );
}

/** A job's status as the shared status pill shows it. */
export const JOB_STATUS: Record<JobResource['status'], JobStatus> = {
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

const FINISHED: readonly JobResource['status'][] = [
  'completed',
  'completedWithErrors',
  'failed',
  'cancelled',
];

export function isJobFinished(job: JobResource): boolean {
  return FINISHED.includes(job.status);
}

/** Both phases are over: nothing more will change on the import's detail page. */
export function isJobSettled(job: JobResource): boolean {
  if (!isJobFinished(job)) return false;
  return job.indexed.state === 'current' || job.status === 'failed' || job.status === 'cancelled';
}

/** "Saved" phase progress: committed (or failed) chunks of all chunks; null while the file is still being read. */
export function savedProgress(job: JobResource): { value: number; max: number } | null {
  const total = Number(job.committed.chunksTotal);
  if (!total) return isJobFinished(job) ? { value: 1, max: 1 } : null;
  const done =
    Number(job.committed.chunksCommitted) +
    Number(job.committed.chunksFailed) +
    Number(job.committed.chunksCancelled);
  return { value: Math.min(done, total), max: total };
}

/** "Searchable" phase progress: applied index tasks of all tasks, full once the job's index state is current. */
export function searchableProgress(job: JobResource): {
  value: number;
  max: number;
  current: boolean;
} {
  const total = Math.max(1, Number(job.indexed.indexTasksTotal));
  const current = job.indexed.state === 'current' && isJobFinished(job);
  return {
    value: current ? total : Math.min(total, Number(job.indexed.indexTasksApplied)),
    max: total,
    current,
  };
}

/** Byte size in the units lit-support staff read (KB/MB/GB, 1024-based). */
export function formatBytes(bytes: number, locale: string): string {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const digits = unit === 0 ? 0 : 1;
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: digits }).format(value)} ${units[unit]}`;
}
