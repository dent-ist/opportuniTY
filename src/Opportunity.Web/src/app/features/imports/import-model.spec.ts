import type { ColumnPreview, JobResource, TargetPreview } from '../../core/api/generated/models';
import {
  DEFAULT_LOAD_FILE,
  DEFAULT_OVERLAY,
  DEFAULT_PATHS,
  IGNORE,
  SEVERAL,
  WizardSettings,
  buildProfile,
  columnFor,
  columnStatus,
  defaultImportName,
  describeDelimiter,
  detectPreset,
  isJobSettled,
  mapsControlNumber,
  savedProgress,
  searchableProgress,
  selectionOf,
  settingsFromProfile,
  stepsFor,
  targetTypes,
  typeLabel,
  volumeRootError,
} from './import-model';

const settings = (over: Partial<WizardSettings> = {}): WizardSettings => ({
  mode: 'append',
  loadFile: DEFAULT_LOAD_FILE,
  overlay: DEFAULT_OVERLAY,
  paths: DEFAULT_PATHS,
  imageMatchBy: 'controlNumber',
  columns: {},
  ...over,
});

function target(label: string, over: Partial<TargetPreview> = {}): TargetPreview {
  return {
    target: { kind: 'field', fieldId: 1, fieldName: label },
    label,
    type: 'keyword',
    isMultiValue: false,
    resolution: 'resolved',
    matchedBy: 'exactName',
    alias: null,
    ...over,
  };
}

function column(name: string, over: Partial<ColumnPreview> = {}): ColumnPreview {
  return {
    column: name,
    index: 0,
    status: 'mapped',
    targets: [],
    mergedInto: null,
    sampleValues: [],
    valueCount: 0,
    blankCount: 0,
    errorCount: 0,
    warningCount: 0,
    ...over,
  };
}

function job(over: Partial<JobResource> = {}): JobResource {
  return {
    jobId: 'job-1',
    workspaceId: 'ws-1',
    jobType: 'import',
    status: 'running',
    statusReason: null,
    initiatedBy: 'u',
    targetSnapshotId: null,
    createdAt: null,
    startedAt: null,
    finishedAt: null,
    committed: {
      chunksTotal: 4,
      chunksCommitted: 1,
      chunksFailed: 1,
      chunksCancelled: 0,
      chunksPending: 2,
      itemsApplied: 0,
      itemsExcludedNoAccess: 0,
      itemsFailed: 0,
      itemsSkippedConcurrentEdit: 0,
      itemsUnchanged: 0,
    },
    indexed: { indexTasksApplied: 1, indexTasksTotal: 3, state: 'indexing' },
    ...over,
  };
}

describe('import wizard rules (E08-T08)', () => {
  it('shows Overlay settings only for modes that change existing documents', () => {
    expect(stepsFor('append').map((s) => s.id)).toEqual([
      'source',
      'format',
      'mapping',
      'files',
      'validate',
    ]);
    expect(stepsFor('overlay').map((s) => s.id)).toContain('overlay');
    expect(stepsFor('appendOverlay').map((s) => s.label)).toEqual([
      'Source & mode',
      'File format',
      'Field mapping',
      'Overlay settings',
      'Natives, text & images',
      'Validate & run',
    ]);
  });

  it('names delimiters by glyph and decimal code', () => {
    expect(describeDelimiter({ char: '\u0014', codepoint: 'U+0014', decimal: 20 })).toBe(
      'DC4 (20)',
    );
    expect(describeDelimiter({ char: 'þ', codepoint: 'U+00FE', decimal: '254' })).toBe('þ (254)');
    expect(describeDelimiter({ char: '\t', codepoint: 'U+0009', decimal: 9 })).toBe('TAB (9)');
    expect(describeDelimiter(null)).toBe('None');
  });

  it('detects the delimiter preset from the first line', () => {
    const bytes = (s: string) => new Uint8Array([...s].map((c) => c.charCodeAt(0)));
    expect(detectPreset(bytes('\u00feA\u00fe\u0014\u00feB\u00fe\r\n'))).toBe('concordance');
    expect(detectPreset(bytes('\u00feA\u00fe\u00b6\u00feB\u00fe\n'))).toBe('concordance-pilcrow');
    expect(detectPreset(bytes('"A","B"\n"x,y","z"'))).toBe('csv');
    expect(detectPreset(bytes('A|B\n'))).toBe('concordance');
  });

  it('accepts only folders inside the import share', () => {
    expect(volumeRootError('')).toBeNull();
    expect(volumeRootError('matter-a/VOL001')).toBeNull();
    expect(volumeRootError('/srv/share')).toMatch(/not an absolute path/);
    expect(volumeRootError('C:\\exports')).toMatch(/not an absolute path/);
    expect(volumeRootError('matter-a/../other')).toMatch(/\.\./);
  });

  it('defaults the import name to the DAT name and the date', () => {
    expect(defaultImportName('VOL001.dat', new Date('2026-10-04T12:00:00Z'))).toBe(
      'VOL001.dat 2026-10-04',
    );
  });

  it('builds the request profile in the wave-8 shape: presets send no characters, custom sends what was entered', () => {
    const preset = buildProfile(
      settings({
        mode: 'overlay',
        loadFile: { ...DEFAULT_LOAD_FILE, delimiters: 'csv', column: ';' },
        overlay: { ...DEFAULT_OVERLAY, blankValuesOverwrite: true, multiValue: 'merge' },
        paths: { ...DEFAULT_PATHS, volumeRoot: ' matter-a\\VOL001 ', stripPrefix: '  ' },
        columns: { NOTES: { column: 'NOTES', ignore: true, targets: [] } },
      }),
    );
    expect(preset).toEqual({
      loadFile: { ...DEFAULT_LOAD_FILE, delimiters: 'csv' },
      mode: 'overlay',
      overlay: {
        keyField: 'ControlNumber',
        blankValuesOverwrite: true,
        multiValue: 'merge',
        allowCodingFields: false,
      },
      paths: {
        volumeRoot: 'matter-a/VOL001',
        stripPrefix: null,
        textInLoadFile: false,
        missingFiles: 'flag',
      },
      images: { matchBy: 'controlNumber' },
      unmappedColumns: 'ignore',
      columns: [{ column: 'NOTES', ignore: true, targets: [] }],
    });
    const custom = buildProfile(
      settings({
        loadFile: { ...DEFAULT_LOAD_FILE, delimiters: 'custom', column: '124', quote: '' },
      }),
    );
    expect(custom.loadFile).toMatchObject({ delimiters: 'custom', column: '124', quote: null });
  });

  it('pre-fills from a saved profile and keeps the wizard values the profile does not set', () => {
    const next = settingsFromProfile(
      {
        profileId: 'p-1',
        name: 'Vendor A',
        definition: {
          mode: 'appendOverlay',
          loadFile: { delimiters: 'concordance-pilcrow', datEncoding: 'utf-16le' },
          overlay: { multiValue: 'merge' },
          paths: { stripPrefix: 'D:\\' },
          columns: [{ column: 'BEGDOC', targets: [{ kind: 'field', fieldId: 1 }] }],
        },
      },
      settings({ paths: { ...DEFAULT_PATHS, volumeRoot: 'vol' } }),
    );
    expect(next.mode).toBe('appendOverlay');
    expect(next.loadFile.delimiters).toBe('concordance-pilcrow');
    expect(next.loadFile.datEncoding).toBe('utf-16le');
    expect(next.overlay).toEqual({ ...DEFAULT_OVERLAY, multiValue: 'merge' });
    expect(next.paths.volumeRoot).toBe('vol');
    expect(next.paths.stripPrefix).toBe('D:\\');
    expect(Object.keys(next.columns)).toEqual(['BEGDOC']);
  });

  it('describes each column: target, type and why, and says when a column is not imported', () => {
    const begdoc = column('BEGDOC', {
      targets: [
        target('Control Number', { matchedBy: 'alias', alias: 'BEGDOC' }),
        target('Begin Bates', { matchedBy: 'alias', alias: 'BEGDOC' }),
      ],
    });
    expect(columnStatus(begdoc)).toBe('Mapped, matched by alias BEGDOC');
    expect(selectionOf(begdoc)).toBe(SEVERAL);
    expect(targetTypes(begdoc)).toBe('Short Text');
    expect(columnStatus(column('NOTES', { status: 'unmapped' }))).toBe('Not mapped: not imported');
    expect(selectionOf(column('NOTES', { status: 'unmapped' }))).toBe('');
    expect(columnStatus(column('X', { status: 'ignored' }))).toBe('Do not import');
    expect(selectionOf(column('X', { status: 'ignored' }))).toBe(IGNORE);
    expect(
      columnStatus(
        column('Y', {
          targets: [target('New', { resolution: 'willCreate', matchedBy: 'profile' })],
        }),
      ),
    ).toBe('Mapped, chosen (new field)');
    expect(typeLabel('boolean')).toBe('Yes/No');
    expect(typeLabel('multiChoice')).toBe('Multiple Choice');
  });

  it('requires Control Number and finds the path columns', () => {
    const cols = [
      column('BEGDOC', { targets: [target('Control Number')] }),
      column('NATIVE', {
        targets: [
          target('Native Path', { target: { kind: 'structural', structural: 'nativePath' } }),
        ],
      }),
    ];
    expect(mapsControlNumber(cols)).toBe(true);
    expect(mapsControlNumber(cols.slice(1))).toBe(false);
    expect(columnFor(cols, 'nativePath')).toBe('NATIVE');
    expect(columnFor(cols, 'textPath')).toBe('');
  });

  it('reads the job in two phases, Saved and Searchable', () => {
    expect(savedProgress(job())).toEqual({ value: 2, max: 4 });
    expect(savedProgress(job({ committed: { ...job().committed, chunksTotal: 0 } }))).toBeNull();
    expect(searchableProgress(job())).toEqual({ value: 1, max: 3, current: false });
    const done = job({
      status: 'completed',
      indexed: { indexTasksApplied: 2, indexTasksTotal: 3, state: 'current' },
    });
    expect(searchableProgress(done)).toEqual({ value: 3, max: 3, current: true });
    expect(isJobSettled(done)).toBe(true);
    expect(isJobSettled(job({ status: 'completed' }))).toBe(false);
    expect(isJobSettled(job({ status: 'failed' }))).toBe(true);
  });
});
