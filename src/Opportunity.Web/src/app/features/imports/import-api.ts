import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { createImportProfile } from '../../core/api/generated/fn/import/create-import-profile';
import { getImport } from '../../core/api/generated/fn/import/get-import';
import { getImportProfile } from '../../core/api/generated/fn/import/get-import-profile';
import { listImportErrors } from '../../core/api/generated/fn/import/list-import-errors';
import { listImportProfiles } from '../../core/api/generated/fn/import/list-import-profiles';
import { listImportTargets } from '../../core/api/generated/fn/import/list-import-targets';
import { listImports } from '../../core/api/generated/fn/import/list-imports';
import type {
  ColumnMapping,
  ImportMode,
  ImportProfileDefinition,
  ImportProfileSummary,
  ImportResource,
  ImportRowIssueResource,
  ImportTargetResource,
  MappingPreviewResult,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

// The backends of the Imports section (E08-T08) as a port. Existing routes go through the generated client; the wave-8
// routes (pre-flight, import report CSV, error file, import modes; owned by #80 and #81) are written by hand against the
// shared contract until they are in the OpenAPI document. The e2e mock API (e2e/support/mock-imports.ts) serves the
// same shapes. Provided by each Imports page, so it is workspace-scoped and replaceable in tests.

export type { ImportMode };

/** Overlay options of an Overlay or Append/Overlay import (WAVE-8 contract "Import modes", #81). */
export interface OverlayOptions {
  /** Unique field matching rows to existing documents (the overlay key); default Control Number. */
  readonly keyField: string;
  /** Blank load-file values clear existing values ("Clear existing values"); default false ("Leave existing values"). */
  readonly blankValuesOverwrite: boolean;
  /** Multiple-choice and multi-value fields: replace the existing values or merge into them. */
  readonly multiValue: 'replace' | 'merge';
  /** Q-31: coding and privilege fields may be overlaid (Workspace Admin, audited). */
  readonly allowCodingFields: boolean;
}

/** Delimiters and encodings of the DAT (`profile.loadFile`). Characters are given as the character or a decimal code. */
export interface LoadFileOptions {
  /** Preset name (`concordance`, `concordance-pilcrow`, `csv`) or `custom`. */
  readonly delimiters: string;
  readonly column: string | null;
  readonly quote: string | null;
  readonly newline: string | null;
  readonly multiValue: string | null;
  /** `auto`, `utf-8`, `utf-16le`, `utf-16be` or `windows-1252`. */
  readonly datEncoding: string;
  readonly textEncoding: string;
  readonly firstLineContainsFieldNames: boolean;
}

export interface PathOptions {
  /** Volume folder inside the import share that relative native/text/image paths resolve against; null = the share. */
  readonly volumeRoot: string | null;
  readonly stripPrefix: string | null;
  readonly textInLoadFile: boolean;
  readonly missingFiles: 'flag' | 'error';
}

/** The `profile` of an import request and of a saved import profile, in the wave-8 contract's shape. */
export interface ImportProfileDraft {
  readonly loadFile: LoadFileOptions;
  readonly mode: ImportMode;
  readonly overlay: OverlayOptions;
  readonly paths: PathOptions;
  readonly images: { readonly matchBy: 'controlNumber' | 'begBates' };
  readonly unmappedColumns: 'ignore';
  readonly columns: readonly ColumnMapping[];
}

/** JSON `request` part of `POST …/imports` and `POST …/imports/preflight`. */
export interface ImportRequest {
  readonly name: string | null;
  readonly mode: ImportMode;
  readonly autoMap: boolean;
  readonly profile: ImportProfileDraft;
}

/** The load file and optional OPT of an import (multipart parts `file` and `opt`). */
export interface ImportFiles {
  readonly dat: Blob;
  readonly datName: string;
  readonly opt: Blob | null;
  readonly optName: string | null;
}

export type IssueSeverity = 'error' | 'warning';

/** One pre-flight finding (row 0 for file-level findings). */
export interface PreflightIssue {
  readonly row: number;
  readonly controlNumber: string | null;
  readonly column: string | null;
  readonly code: string;
  readonly severity: IssueSeverity;
  readonly message: string;
}

/** `ImportPreflightResource` (WAVE-8 contract "Pre-flight", #80). */
export interface PreflightResult {
  readonly preflightId: string;
  readonly rowsRead: number;
  readonly errorCount: number;
  readonly warningCount: number;
  /** Any error: the wizard does not start the import. */
  readonly blocking: boolean;
  readonly issueCounts: readonly { code: string; severity: IssueSeverity; count: number }[];
  /** The first 200 issues; all of them are in the CSV download. */
  readonly issues: readonly PreflightIssue[];
  readonly mode: ImportMode;
}

/** A page of the import history. */
export interface ImportPage {
  readonly items: readonly ImportResource[];
  readonly nextCursor: string | null;
}

/** A saved import profile: its name and the definition it pre-fills the wizard with. */
export interface SavedProfile {
  readonly profileId: string;
  readonly name: string;
  readonly definition: ImportProfileDefinition;
}

@Injectable()
export abstract class ImportApi {
  /** `GET …/imports`: the import history, newest first. */
  abstract list(cursor?: string | null): Promise<ImportPage>;
  /** `GET …/imports/{id}`: the import with its job (Saved and Searchable progress) and report counters. */
  abstract get(importId: string): Promise<ImportResource>;
  /** `GET …/imports/{id}/errors`: the first row errors of the import report. */
  abstract errors(importId: string, limit: number): Promise<readonly ImportRowIssueResource[]>;
  /** `GET …/import-targets`: fields and structural targets a column can map to, structural targets first. */
  abstract targets(): Promise<readonly ImportTargetResource[]>;
  /** `GET …/import-profiles`. */
  abstract profiles(): Promise<readonly ImportProfileSummary[]>;
  /** `GET …/import-profiles/{id}`. */
  abstract profile(profileId: string): Promise<SavedProfile>;
  /** `POST …/import-profiles`: saves the wizard's settings and mapping under a new name. */
  abstract saveProfile(name: string, definition: ImportProfileDraft): Promise<SavedProfile>;
  /**
   * `POST …/import-mapping-previews`: parses the leading bytes of the DAT with the settings, auto-maps the rest and
   * coerces `rows` data rows.
   */
  abstract preview(
    sample: Blob,
    fileName: string,
    sampleIsPartial: boolean,
    profile: ImportProfileDraft,
    autoMap: boolean,
    rows: number,
  ): Promise<MappingPreviewResult>;
  /** `POST …/imports/preflight`: validates the whole load file without writing anything. */
  abstract preflight(files: ImportFiles, request: ImportRequest): Promise<PreflightResult>;
  /** `POST …/imports` (202 + job). */
  abstract start(
    files: ImportFiles,
    request: ImportRequest,
    idempotencyKey: string,
  ): Promise<ImportResource>;
  /** `GET …/imports/preflight/{preflightId}/issues`: every pre-flight issue as CSV (gateway download). */
  abstract preflightIssuesUrl(preflightId: string): string;
  /** `GET …/imports/{id}/report.csv` (gateway download). */
  abstract reportCsvUrl(importId: string): string;
  /** `GET …/imports/{id}/error-file`: the failed rows in the source's delimiters and encoding (gateway download). */
  abstract errorFileUrl(importId: string): string;
}

const IMPORTS = 'imports';

@Injectable()
export class HttpImportApi extends ImportApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  private get workspaceId(): string {
    return this.context.workspaceId;
  }

  list(cursor?: string | null): Promise<ImportPage> {
    return firstValueFrom(
      listImports(this.http, this.rootUrl, {
        workspaceId: this.workspaceId,
        limit: 50,
        ...(cursor ? { cursor } : {}),
      }).pipe(map((r) => ({ items: r.body.items, nextCursor: r.body.nextCursor }))),
    );
  }

  get(importId: string): Promise<ImportResource> {
    return firstValueFrom(
      getImport(this.http, this.rootUrl, { workspaceId: this.workspaceId, importId }).pipe(
        map((r) => r.body),
      ),
    );
  }

  errors(importId: string, limit: number): Promise<readonly ImportRowIssueResource[]> {
    return firstValueFrom(
      listImportErrors(this.http, this.rootUrl, {
        workspaceId: this.workspaceId,
        importId,
        limit,
      }).pipe(map((r) => r.body.items)),
    );
  }

  async targets(): Promise<readonly ImportTargetResource[]> {
    const page = await firstValueFrom(
      listImportTargets(this.http, this.rootUrl, { workspaceId: this.workspaceId }),
    );
    return page.body.items;
  }

  async profiles(): Promise<readonly ImportProfileSummary[]> {
    const page = await firstValueFrom(
      listImportProfiles(this.http, this.rootUrl, { workspaceId: this.workspaceId }),
    );
    return page.body.items;
  }

  async profile(profileId: string): Promise<SavedProfile> {
    const r = await firstValueFrom(
      getImportProfile(this.http, this.rootUrl, { workspaceId: this.workspaceId, profileId }),
    );
    return { profileId: r.body.profileId, name: r.body.name, definition: r.body.definition };
  }

  async saveProfile(name: string, definition: ImportProfileDraft): Promise<SavedProfile> {
    const r = await firstValueFrom(
      createImportProfile(this.http, this.rootUrl, {
        workspaceId: this.workspaceId,
        // The wizard's draft (column drafts, overlay options) is wider than the generated definition type.
        body: { name, definition: definition as unknown as ImportProfileDefinition },
      }),
    );
    return { profileId: r.body.profileId, name: r.body.name, definition: r.body.definition };
  }

  preview(
    sample: Blob,
    fileName: string,
    sampleIsPartial: boolean,
    profile: ImportProfileDraft,
    autoMap: boolean,
    rows: number,
  ): Promise<MappingPreviewResult> {
    const form = new FormData();
    form.set('file', sample, fileName);
    form.set('request', JSON.stringify({ profile, autoMap, rows, sampleIsPartial }));
    return firstValueFrom(
      this.http.post<MappingPreviewResult>(this.context.apiUrl('import-mapping-previews'), form),
    );
  }

  preflight(files: ImportFiles, request: ImportRequest): Promise<PreflightResult> {
    return firstValueFrom(
      this.http
        .post<PreflightResult>(
          this.context.apiUrl(IMPORTS, 'preflight'),
          importForm(files, request),
        )
        .pipe(map(toPreflightResult)),
    );
  }

  start(
    files: ImportFiles,
    request: ImportRequest,
    idempotencyKey: string,
  ): Promise<ImportResource> {
    return firstValueFrom(
      this.http.post<ImportResource>(this.context.apiUrl(IMPORTS), importForm(files, request), {
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  preflightIssuesUrl(preflightId: string): string {
    return this.context.apiUrl(IMPORTS, 'preflight', preflightId, 'issues');
  }

  reportCsvUrl(importId: string): string {
    return this.context.apiUrl(IMPORTS, importId, 'report.csv');
  }

  errorFileUrl(importId: string): string {
    return this.context.apiUrl(IMPORTS, importId, 'error-file');
  }
}

/** The multipart body shared by pre-flight and start: `file`, optional `opt`, and the JSON `request`. */
export function importForm(files: ImportFiles, request: ImportRequest): FormData {
  const form = new FormData();
  form.set('file', files.dat, files.datName);
  if (files.opt) form.set('opt', files.opt, files.optName ?? 'images.opt');
  form.set('request', JSON.stringify(request));
  return form;
}

/** Normalises numbers (the API may send int64 as strings) and caps nothing: the server already sends the first 200. */
export function toPreflightResult(body: PreflightResult): PreflightResult {
  return {
    preflightId: body.preflightId,
    rowsRead: Number(body.rowsRead),
    errorCount: Number(body.errorCount),
    warningCount: Number(body.warningCount),
    blocking: !!body.blocking || Number(body.errorCount) > 0,
    issueCounts: (body.issueCounts ?? []).map((c) => ({ ...c, count: Number(c.count) })),
    issues: (body.issues ?? []).map((i) => ({
      row: Number(i.row),
      controlNumber: i.controlNumber ?? null,
      column: i.column ?? null,
      code: i.code,
      severity: i.severity,
      message: i.message,
    })),
    mode: body.mode,
  };
}
