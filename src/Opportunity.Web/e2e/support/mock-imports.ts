import type { Route } from '@playwright/test';

/**
 * Imports for the e2e mock API (E08-T08): import targets, import profiles, the mapping preview, the wave-8 pre-flight
 * (`POST …/imports/preflight`, #80), starting an import and its progress, the import history and row errors, and the
 * gateway downloads (pre-flight issues CSV, import report CSV, error file). Shapes follow the generated client and the
 * wave-8 contract; the import advances one phase per read of `GET …/imports/{id}`.
 */

const HEADER = ['BEGDOC', 'ENDDOC', 'CUSTODIAN', 'DATESENT', 'NATIVEPATH', 'TEXTPATH', 'NOTES'];

type Target = {
  kind: 'field' | 'structural';
  fieldId?: number;
  fieldName?: string;
  structural?: string;
};

interface TargetDef {
  target: Target;
  label: string;
  type: string;
  isCodingField?: boolean;
  isStructural?: boolean;
  aliases: string[];
}

const field = (id: number, name: string, type: string, aliases: string[] = [], extra = {}) => ({
  target: { kind: 'field' as const, fieldId: id, fieldName: name },
  label: name,
  type,
  aliases,
  ...extra,
});
const structural = (name: string, label: string, aliases: string[]) => ({
  target: { kind: 'structural' as const, structural: name },
  label,
  type: 'path',
  aliases,
  isStructural: true,
});

const TARGETS: TargetDef[] = [
  field(1, 'Control Number', 'keyword', ['BEGDOC'], { isStructural: true }),
  field(2, 'Begin Bates', 'keyword', ['BEGDOC', 'BEGBATES'], { isStructural: true }),
  field(3, 'End Bates', 'keyword', ['ENDDOC', 'ENDBATES'], { isStructural: true }),
  structural('parentId', 'Parent ID', ['PARENTID']),
  structural('nativePath', 'Native Path', ['NATIVEPATH', 'NATIVELINK']),
  structural('textPath', 'Extracted Text Path', ['TEXTPATH', 'TEXTLINK']),
  field(10, 'Custodian', 'keyword'),
  field(11, 'Date Sent', 'dateTime'),
  field(100, 'Responsiveness', 'singleChoice', [], { isCodingField: true }),
];

function targetResource(t: TargetDef) {
  return {
    target: t.target,
    label: t.label,
    type: t.type,
    isMultiValue: false,
    isStructural: !!t.isStructural,
    isCodingField: !!t.isCodingField,
    isSecurityAffecting: false,
    autoMapEligible: !t.isCodingField,
    aliases: t.aliases,
  };
}

const norm = (s: string) => s.replace(/[^a-z0-9]/gi, '').toLowerCase();
const sameTarget = (a: Target, b: Target) =>
  a.kind === b.kind && a.fieldId === b.fieldId && a.structural === b.structural;

interface ColumnMapping {
  column: string;
  ignore?: boolean;
  targets?: Target[];
}

interface ImportRequestBody {
  name?: string | null;
  mode?: string;
  autoMap?: boolean;
  profile?: { columns?: ColumnMapping[]; mode?: string; loadFile?: Record<string, unknown> };
}

/** Auto-maps the fixed header like the API: aliases, exact names, normalised names. */
function autoTargets(column: string) {
  const matches: { def: TargetDef; matchedBy: string; alias: string | null }[] = [];
  for (const def of TARGETS) {
    if (def.isCodingField) continue;
    if (def.aliases.includes(column)) matches.push({ def, matchedBy: 'alias', alias: column });
    else if (def.label.toUpperCase() === column)
      matches.push({ def, matchedBy: 'exactName', alias: null });
    else if (norm(def.label) === norm(column))
      matches.push({ def, matchedBy: 'normalizedName', alias: null });
  }
  return matches;
}

function sampleValue(column: string, row: number): string {
  const n = String(row).padStart(7, '0');
  switch (column) {
    case 'BEGDOC':
    case 'ENDDOC':
      return `IMP${n}`;
    case 'CUSTODIAN':
      return row % 2 ? 'Jordan Lee' : 'Sam Patel';
    case 'DATESENT':
      return `2024-03-${String((row % 28) + 1).padStart(2, '0')}`;
    case 'NATIVEPATH':
      return `D:\\Processing\\VOL001\\NATIVES\\IMP${n}.msg`;
    case 'TEXTPATH':
      return `VOL001\\TEXT\\IMP${n}.txt`;
    default:
      return row % 3 ? '' : 'Follow up';
  }
}

/** `MappingPreviewResult` for the fixed header with the request's mapping. */
export function mappingPreview(body: ImportRequestBody & { rows?: number }) {
  const explicit = new Map((body.profile?.columns ?? []).map((c) => [c.column, c]));
  const autoMap = body.autoMap !== false;
  const columns = HEADER.map((column, index) => {
    const chosen = explicit.get(column);
    let status = 'unmapped';
    let targets: unknown[] = [];
    if (chosen?.ignore) status = 'ignored';
    else if (chosen?.targets?.length) {
      status = 'mapped';
      targets = chosen.targets.map((t) => {
        const def = TARGETS.find((d) => sameTarget(d.target, t));
        return {
          target: t,
          label: def?.label ?? t.fieldName ?? String(t.structural),
          type: def?.type ?? 'text',
          isMultiValue: false,
          resolution: 'resolved',
          matchedBy: 'profile',
          alias: null,
        };
      });
    } else if (autoMap) {
      const found = autoTargets(column);
      if (found.length) {
        status = 'mapped';
        targets = found.map((m) => ({
          target: m.def.target,
          label: m.def.label,
          type: m.def.type,
          isMultiValue: false,
          resolution: 'resolved',
          matchedBy: m.matchedBy,
          alias: m.alias,
        }));
      }
    }
    return {
      column,
      index,
      status,
      targets,
      mergedInto: null,
      sampleValues: [1, 2, 3].map((r) => sampleValue(column, r)),
      valueCount: 20,
      blankCount: 0,
      errorCount: 0,
      warningCount: 0,
    };
  });
  const mapped = columns.filter((c) => c.status === 'mapped');
  const rows = Array.from({ length: Math.min(20, body.rows ?? 20) }, (_, i) => ({
    rowNumber: i + 1,
    lineNumber: i + 2,
    controlNumber: `IMP${String(i + 1).padStart(7, '0')}`,
    rejected: false,
    parserIssues: [],
    errorCount: 0,
    cells: mapped.flatMap((c) =>
      (c.targets as { label: string }[]).map((t) => ({
        column: c.column,
        target: t.label,
        raw: sampleValue(c.column, i + 1),
        value: sampleValue(c.column, i + 1),
        error: null,
        warnings: [],
      })),
    ),
  }));
  const csv = body.profile?.loadFile?.['delimiters'] === 'csv';
  const info = (char: string) => ({
    char,
    codepoint: `U+${char.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')}`,
    decimal: char.charCodeAt(0),
  });
  return {
    canImport: true,
    columns,
    effectiveProfile: {
      columns: columns
        .filter((c) => c.status !== 'unmapped')
        .map((c) => ({
          column: c.column,
          ignore: c.status === 'ignored',
          targets: (c.targets as { target: Target }[]).map((t) => t.target),
        })),
    },
    file: {
      encoding: 'utf-8 (BOM)',
      encodingSource: 'byteOrderMark',
      delimiters: csv ? 'csv' : 'concordance',
      column: info(csv ? ',' : '\u0014'),
      quote: info(csv ? '"' : 'þ'),
      newline: csv ? null : info('®'),
      multiValue: info(';'),
      nestedValue: info('\\'),
      header: HEADER,
      misdecodeSuspected: false,
      parserIssues: [],
    },
    issues: [],
    missingColumns: [],
    newColumns: [],
    newFields: [],
    rows,
  };
}

function importResource(id: string, name: string, mode: string, polls: number, errored: number) {
  const done = polls >= 1;
  return {
    importId: id,
    workspaceId: 'ws-1',
    name,
    mode,
    sourceFileName: 'VOL001.dat',
    sourceSize: 4096,
    sourceSha256: 'f'.repeat(64),
    profileId: null,
    codingOverlayFieldIds: [],
    report: {
      rowsRead: 20,
      rowsImported: done ? 20 - errored : 8,
      rowsOverlaid: 0,
      rowsSkipped: 0,
      rowsErrored: done ? errored : 0,
      fieldsCreated: 0,
      choicesCreated: 0,
    },
    job: {
      jobId: `job-${id}`,
      workspaceId: 'ws-1',
      jobType: 'import',
      status: done ? (errored ? 'completedWithErrors' : 'completed') : 'running',
      statusReason: null,
      initiatedBy: 'user-1',
      targetSnapshotId: null,
      createdAt: '2026-10-04T09:00:00Z',
      startedAt: '2026-10-04T09:00:01Z',
      finishedAt: done ? '2026-10-04T09:00:09Z' : null,
      committed: {
        chunksTotal: 2,
        chunksCommitted: done ? 2 : 1,
        chunksPending: done ? 0 : 1,
        chunksFailed: 0,
        chunksCancelled: 0,
        itemsApplied: done ? 20 - errored : 8,
        itemsUnchanged: 0,
        itemsSkippedConcurrentEdit: 0,
        itemsFailed: done ? errored : 0,
        itemsExcludedNoAccess: 0,
      },
      indexed: {
        indexTasksTotal: 2,
        indexTasksApplied: polls >= 2 ? 2 : done ? 1 : 0,
        state: polls >= 2 ? 'current' : 'indexing',
      },
    },
    createdAt: '2026-10-04T09:00:00Z',
    completedAt: done ? '2026-10-04T09:00:09Z' : null,
    optFileName: null,
    imagesOnly: false,
  };
}

export interface ImportMockOptions {
  /** The pre-flight finds errors (blocking) instead of warnings only. */
  preflightBlocking?: boolean;
}

/** A recorded import request: the parsed JSON `request` part, the file names and the Idempotency-Key. */
export interface ImportRequestRecord {
  request: ImportRequestBody;
  files: string[];
  idempotencyKey: string | null;
}

export class ImportsMock {
  readonly preflights: ImportRequestRecord[] = [];
  readonly starts: ImportRequestRecord[] = [];
  readonly previews: ImportRequestBody[] = [];
  private readonly imports = new Map<string, { name: string; mode: string; polls: number }>([
    ['imp-1', { name: 'VOL001.dat 2026-10-01', mode: 'append', polls: 2 }],
  ]);
  private readonly profiles = [
    {
      profileId: 'profile-1',
      name: 'Standard volume',
      description: null,
      mode: 'append',
      columnCount: 0,
      updatedAt: '2026-10-01T09:00:00Z',
      version: 1,
      definition: { mode: 'append', loadFile: { delimiters: 'concordance' }, columns: [] },
    },
  ];

  constructor(private readonly options: ImportMockOptions = {}) {}

  /** Answers an import route, or returns undefined. */
  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const m =
      /^\/api\/v1\/workspaces\/[^/]+\/(import-targets|import-profiles|import-mapping-previews|imports)(?:\/(.*))?$/.exec(
        path,
      );
    if (!m) return undefined;
    const [, area, rest = ''] = m;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    const page = (items: unknown[]) =>
      json({ items, nextCursor: null, total: { value: items.length, relation: 'eq' } });

    if (area === 'import-targets' && method === 'GET') return page(TARGETS.map(targetResource));
    if (area === 'import-profiles') {
      if (!rest && method === 'GET')
        return page(this.profiles.map(({ definition: _d, ...summary }) => summary));
      if (!rest && method === 'POST') {
        const body = route.request().postDataJSON() as { name: string; definition: unknown };
        const profile = {
          profileId: `profile-${this.profiles.length + 1}`,
          name: body.name,
          description: null,
          mode: 'append',
          columnCount: 0,
          updatedAt: '2026-10-04T09:00:00Z',
          version: 1,
          definition: body.definition as (typeof this.profiles)[number]['definition'],
        };
        this.profiles.push(profile);
        return json(this.resource(profile), 201);
      }
      const profile = this.profiles.find((p) => p.profileId === rest);
      if (profile && method === 'GET') return json(this.resource(profile));
      return undefined;
    }
    if (area === 'import-mapping-previews' && method === 'POST') {
      const request = requestPart(route) as ImportRequestBody & { rows?: number };
      this.previews.push(request);
      return json(mappingPreview(request));
    }
    // area === 'imports'
    if (!rest && method === 'GET') {
      return page(
        [...this.imports]
          .reverse()
          .map(([id, i]) => importResource(id, i.name, i.mode, i.polls, 2)),
      );
    }
    if (rest === 'preflight' && method === 'POST') {
      const record = this.record(route);
      this.preflights.push(record);
      const mode = record.request.mode ?? 'append';
      const blocking = !!this.options.preflightBlocking;
      const issues = [
        ...(blocking
          ? [
              {
                row: 4,
                controlNumber: 'IMP0000004',
                column: 'BEGDOC',
                code: mode === 'append' ? 'KEY_EXISTS' : 'KEY_MISSING',
                severity: 'error',
                message: 'A document with this Control Number already exists.',
              },
            ]
          : []),
        {
          row: 7,
          controlNumber: 'IMP0000007',
          column: 'NATIVEPATH',
          code: 'NATIVE_MISSING',
          severity: 'warning',
          message: 'The native file is not in the volume folder.',
        },
        {
          row: 12,
          controlNumber: 'IMP0000012',
          column: 'TEXTPATH',
          code: 'TEXT_MISSING',
          severity: 'warning',
          message: 'The extracted text file is not in the volume folder.',
        },
      ];
      return json({
        preflightId: `preflight-${this.preflights.length}`,
        rowsRead: 20,
        errorCount: blocking ? 1 : 0,
        warningCount: 2,
        blocking,
        issueCounts: issues.map((i) => ({ code: i.code, severity: i.severity, count: 1 })),
        issues,
        mode,
      });
    }
    if (/^preflight\/[^/]+\/issues$/.test(rest) && method === 'GET') {
      return route.fulfill({
        status: 200,
        contentType: 'text/csv',
        headers: { 'Content-Disposition': 'attachment; filename="preflight-issues.csv"' },
        body: 'Row,ControlNumber,Column,Severity,Code,Message\n',
      });
    }
    if (!rest && method === 'POST') {
      const record = this.record(route);
      this.starts.push(record);
      const id = `imp-${this.imports.size + 1}`;
      const name = record.request.name ?? 'VOL001.dat 2026-10-04';
      const mode = record.request.mode ?? 'append';
      this.imports.set(id, { name, mode, polls: 0 });
      return json(importResource(id, name, mode, 0, 2), 202);
    }
    const one = /^([^/]+)(?:\/(errors|report\.csv|error-file))?$/.exec(rest);
    const item = one && this.imports.get(one[1]);
    if (one && item && method === 'GET') {
      if (!one[2]) {
        const body = importResource(one[1], item.name, item.mode, item.polls, 2);
        item.polls++;
        return json(body);
      }
      if (one[2] === 'errors') {
        return page(
          [3, 9].map((row) => ({
            row,
            line: row + 1,
            severity: 'error',
            controlNumber: `IMP${String(row).padStart(7, '0')}`,
            column: 'DATESENT',
            code: 'DATE_UNPARSEABLE',
            message: 'The date could not be read.',
            file: 'dat',
          })),
        );
      }
      return route.fulfill({
        status: 200,
        contentType: one[2] === 'report.csv' ? 'text/csv' : 'application/octet-stream',
        headers: { 'Content-Disposition': 'attachment' },
        body: '',
      });
    }
    return undefined;
  }

  private resource(p: (typeof this.profiles)[number]) {
    return {
      profileId: p.profileId,
      workspaceId: 'ws-1',
      name: p.name,
      description: null,
      definition: p.definition,
      version: 1,
      createdAt: p.updatedAt,
      createdBy: 'user-1',
      updatedAt: p.updatedAt,
      updatedBy: 'user-1',
    };
  }

  private record(route: Route): ImportRequestRecord {
    const raw = route.request().postDataBuffer()?.toString('latin1') ?? '';
    return {
      request: requestPart(route) as ImportRequestBody,
      files: [...raw.matchAll(/name="(file|opt)"; filename="([^"]*)"/g)].map(
        (f) => `${f[1]}:${f[2]}`,
      ),
      idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
    };
  }
}

/** A small Concordance-style DAT (DC4 columns, þ quotes) with the mock's header, for the wizard's file chooser. */
export const DEMO_DAT = {
  name: 'VOL001.dat',
  mimeType: 'text/plain',
  buffer: Buffer.from(
    HEADER.map((h) => `þ${h}þ`).join('\u0014') +
      '\r\n' +
      HEADER.map((h) => `þ${sampleValue(h, 1)}þ`).join('\u0014') +
      '\r\n',
    'latin1',
  ),
};

/** The JSON `request` part of a multipart body. */
function requestPart(route: Route): unknown {
  const raw = route.request().postDataBuffer()?.toString('utf8') ?? '';
  const match = /name="request"\r\n(?:[^\r\n]+\r\n)*\r\n([\s\S]*?)\r\n--/.exec(raw);
  return match ? JSON.parse(match[1]) : {};
}
