import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { getDocumentCoding } from '../../../core/api/generated/fn/coding/get-document-coding';
import { getDocumentCodingHistory } from '../../../core/api/generated/fn/coding/get-document-coding-history';
import { saveDocumentCoding } from '../../../core/api/generated/fn/coding/save-document-coding';
import { generatePrivilegeLog } from '../../../core/api/generated/fn/privilege-logs/generate-privilege-log';
import { getPrivilegeLogChanges } from '../../../core/api/generated/fn/privilege-logs/get-privilege-log-changes';
import { listPrivilegeLogEntries } from '../../../core/api/generated/fn/privilege-logs/list-privilege-log-entries';
import { listPrivilegeLogTemplates } from '../../../core/api/generated/fn/privilege-logs/list-privilege-log-templates';
import { listPrivilegeLogs } from '../../../core/api/generated/fn/privilege-logs/list-privilege-logs';
import { listProductions } from '../../../core/api/generated/fn/productions/list-productions';
import { listSavedSearches } from '../../../core/api/generated/fn/saved-searches/list-saved-searches';
import type {
  GeneratePrivilegeLogRequest,
  PrivilegeLogResource,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// Privilege logs (E13-T03, #182) as a port over the generated client: finalized productions and saved searches to log,
// templates and presets, generation, versions with their entries and what changed since the previous version, the
// per-document description (the Privilege Description field, edited through coding: audited, with history), and the
// CSV/XLSX downloads through the protected-content gateway. Provided by the page, so it is workspace-scoped.

/** The Privilege Description system field (E13-T01): the reviewer-written per-document description (Q-20). */
export const PRIVILEGE_DESCRIPTION_FIELD = 39;

export type LogPreset = 'documentByDocument' | 'metadataOnly';

/** A template or a built-in preset, as the generator chooses it. */
export interface LogTemplateChoice {
  /** `preset:<name>` or `template:<id>`. */
  readonly key: string;
  readonly name: string;
  readonly columns: readonly string[];
}

/** What a log is generated from. */
export type LogSource =
  | { readonly kind: 'production'; readonly id: string; readonly reviewSetId?: string | null }
  | { readonly kind: 'savedSearch'; readonly id: string }
  | { readonly kind: 'snapshot'; readonly id: string };

export interface SavedSearchChoice {
  readonly savedSearchId: string;
  readonly name: string;
}

export interface FinalizedProduction {
  readonly productionId: string;
  readonly name: string;
  readonly version: number;
  readonly batesRange: string;
}

export interface AppliedRule {
  readonly label: string;
  readonly conditions: string;
  readonly excluded: number;
}

export interface LogFile {
  readonly format: 'csv' | 'xlsx';
  readonly sha256: string;
  readonly bytes: number;
}

export interface LogVersion {
  readonly logId: string;
  readonly version: number;
  readonly source: 'production' | 'savedSearch' | 'snapshot';
  readonly productionId: string | null;
  readonly productionName: string | null;
  readonly savedSearchId: string | null;
  readonly savedSearchName: string | null;
  readonly templateId: string | null;
  /** The column holding Privilege Description, if the version has one. */
  readonly descriptionColumn: number | null;
  readonly snapshotId: string;
  readonly reviewSetSnapshotId: string | null;
  readonly templateName: string;
  readonly contentSha256: string;
  readonly files: readonly LogFile[];
  readonly entries: number;
  readonly withheld: number;
  readonly redacted: number;
  readonly excludedByRules: number;
  readonly privacyRedactionsIncluded: boolean;
  readonly columns: readonly string[];
  readonly rules: readonly AppliedRule[];
  readonly generatedBy: string;
  readonly generatedAt: string;
}

export type LogTreatment = 'withheld' | 'redacted' | 'redactedPrivacy';

export interface LogEntry {
  readonly ordinal: number;
  readonly documentId: string;
  readonly treatment: LogTreatment;
  readonly cells: readonly string[];
}

export interface Page<T> {
  readonly items: readonly T[];
  readonly nextCursor: string | null;
  readonly total: number;
}

export interface CellChange {
  readonly column: string;
  readonly before: string;
  readonly after: string;
}

export interface EntryChange {
  readonly kind: 'added' | 'removed' | 'changed';
  readonly documentId: string;
  readonly treatment: LogTreatment;
  readonly cells: readonly string[];
  readonly changedCells: readonly CellChange[];
}

export interface LogChanges {
  /** Null when there is no earlier version to compare with. */
  readonly previousVersion: number | null;
  readonly added: number;
  readonly removed: number;
  readonly changed: number;
  readonly unchanged: number;
  readonly columnsAdded: readonly string[];
  readonly columnsRemoved: readonly string[];
  readonly items: readonly EntryChange[];
  readonly nextCursor: string | null;
}

/** A document's current Privilege Description, the version to save against, and whether the caller may edit it. */
export interface Description {
  readonly value: string;
  readonly documentVersion: string;
  readonly editable: boolean;
}

export interface DescriptionChange {
  readonly at: string;
  readonly by: string;
  readonly before: string;
  readonly after: string;
}

export interface Generation {
  readonly log: LogVersion;
  /** The latest version already had this content: nothing new was stored. */
  readonly unchanged: boolean;
}

@Injectable()
export abstract class PrivilegeLogApi {
  /** Finalized productions, newest first (`GET …/productions`; needs Production.Create). */
  abstract productions(): Promise<readonly FinalizedProduction[]>;
  /** Presets first, then the workspace's templates by name (`GET …/privilege-log-templates`). */
  abstract templates(): Promise<readonly LogTemplateChoice[]>;
  /** Saved searches the caller can see, by name (`GET …/saved-searches`; needs Search.Execute). */
  abstract savedSearches(): Promise<readonly SavedSearchChoice[]>;
  /** `GET …/privilege-logs` newest first; only versions the caller may read. */
  abstract versions(): Promise<readonly LogVersion[]>;
  /** `POST …/privilege-logs`: a new version, or the unchanged latest one. */
  abstract generate(source: LogSource, templateKey: string): Promise<Generation>;
  /** `GET …/privilege-logs/{id}/entries`: a page of rows in log order. */
  abstract entries(logId: string, cursor?: string | null): Promise<Page<LogEntry>>;
  /** `GET …/privilege-logs/{id}/changes`: what changed since the previous version. */
  abstract changes(logId: string, cursor?: string | null): Promise<LogChanges>;
  /** `GET …/documents/{id}/coding`: the document's Privilege Description now. */
  abstract description(documentId: string): Promise<Description>;
  /** `PUT …/documents/{id}/coding` (If-Match, Idempotency-Key; audited `Coding.Changed`). */
  abstract saveDescription(
    documentId: string,
    documentVersion: string,
    value: string,
  ): Promise<Description>;
  /** `GET …/documents/{id}/coding-history?fieldId=39`, newest first. */
  abstract descriptionHistory(documentId: string): Promise<readonly DescriptionChange[]>;
  /** `GET …/privilege-logs/{id}/content?format=` (protected-content gateway download, audited). */
  abstract downloadUrl(logId: string, format: 'csv' | 'xlsx'): string;
}

@Injectable()
export class HttpPrivilegeLogApi extends PrivilegeLogApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  async productions(): Promise<readonly FinalizedProduction[]> {
    const response = await firstValueFrom(
      listProductions(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 500,
      }),
    );
    return response.body.items
      .filter((p) => p.status === 'finalized')
      .map((p) => ({
        productionId: p.productionId,
        name: p.name,
        version: Number(p.version),
        batesRange: p.bates.first && p.bates.last ? `${p.bates.first} – ${p.bates.last}` : '',
      }));
  }

  async templates(): Promise<readonly LogTemplateChoice[]> {
    const response = await firstValueFrom(
      listPrivilegeLogTemplates(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    );
    const header = (c: { header?: string | null }) => c.header ?? '';
    return [
      ...response.body.presets.map((p) => ({
        key: `preset:${p.preset}`,
        name: p.name,
        columns: (p.definition.columns ?? []).map(header),
      })),
      ...response.body.items.map((t) => ({
        key: `template:${t.templateId}`,
        name: t.name,
        columns: (t.definition.columns ?? []).map(header),
      })),
    ];
  }

  async savedSearches(): Promise<readonly SavedSearchChoice[]> {
    const response = await firstValueFrom(
      listSavedSearches(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 500,
      }),
    );
    return response.body.items.map((s) => ({ savedSearchId: s.savedSearchId, name: s.name }));
  }

  async versions(): Promise<readonly LogVersion[]> {
    const response = await firstValueFrom(
      listPrivilegeLogs(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 100,
      }),
    );
    return response.body.items.map(toVersion);
  }

  generate(source: LogSource, templateKey: string): Promise<Generation> {
    const [kind, value] = splitKey(templateKey);
    const from: GeneratePrivilegeLogRequest =
      source.kind === 'production'
        ? {
            productionId: source.id,
            ...(source.reviewSetId ? { snapshotId: source.reviewSetId } : {}),
          }
        : source.kind === 'savedSearch'
          ? { savedSearchId: source.id }
          : { snapshotId: source.id };
    return firstValueFrom(
      generatePrivilegeLog(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        body: {
          ...from,
          ...(kind === 'template' ? { templateId: value } : { preset: value as LogPreset }),
        },
      }).pipe(map((r) => ({ log: toVersion(r.body.log), unchanged: r.body.unchanged }))),
    );
  }

  async entries(logId: string, cursor?: string | null): Promise<Page<LogEntry>> {
    const response = await firstValueFrom(
      listPrivilegeLogEntries(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        logId,
        limit: 100,
        ...(cursor ? { cursor } : {}),
      }),
    );
    return {
      items: response.body.items.map((e) => ({
        ordinal: Number(e.ordinal),
        documentId: e.documentId,
        treatment: e.treatment,
        cells: e.cells,
      })),
      nextCursor: response.body.nextCursor ?? null,
      total: Number(response.body.total?.value ?? response.body.items.length),
    };
  }

  async changes(logId: string, cursor?: string | null): Promise<LogChanges> {
    const r = (
      await firstValueFrom(
        getPrivilegeLogChanges(this.http, this.rootUrl, {
          workspaceId: this.context.workspaceId,
          logId,
          limit: 100,
          ...(cursor ? { cursor } : {}),
        }),
      )
    ).body;
    return {
      previousVersion: r.previousVersion == null ? null : Number(r.previousVersion),
      added: Number(r.added),
      removed: Number(r.removed),
      changed: Number(r.changed),
      unchanged: Number(r.unchanged),
      columnsAdded: r.columnsAdded,
      columnsRemoved: r.columnsRemoved,
      items: r.items.map((c) => ({
        kind: c.kind,
        documentId: c.documentId,
        treatment: c.treatment,
        cells: c.cells,
        changedCells: c.changedCells,
      })),
      nextCursor: r.nextCursor ?? null,
    };
  }

  async description(documentId: string): Promise<Description> {
    const response = await firstValueFrom(
      getDocumentCoding(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        documentId,
      }),
    );
    return toDescription(response.body);
  }

  async saveDescription(
    documentId: string,
    documentVersion: string,
    value: string,
  ): Promise<Description> {
    const text = value.trim();
    const response = await firstValueFrom(
      saveDocumentCoding(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        documentId,
        'If-Match': `"${documentVersion}"`,
        'Idempotency-Key':
          globalThis.crypto?.randomUUID?.() ??
          `${Date.now()}-${Math.random().toString(36).slice(2)}`,
        body: {
          changes: [
            { fieldId: PRIVILEGE_DESCRIPTION_FIELD, operation: 'set', value: text ? text : null },
          ],
        },
      }),
    );
    return toDescription(response.body);
  }

  async descriptionHistory(documentId: string): Promise<readonly DescriptionChange[]> {
    const response = await firstValueFrom(
      getDocumentCodingHistory(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        documentId,
        fieldId: PRIVILEGE_DESCRIPTION_FIELD,
        limit: 50,
      }),
    );
    return response.body.items
      .filter((h) => h.kind === 'changed')
      .map((h) => ({
        at: String(h.occurredAt),
        by: h.actor.displayName ?? h.actor.userId,
        before: typeof h.oldValue === 'string' ? h.oldValue : '',
        after: typeof h.newValue === 'string' ? h.newValue : '',
      }));
  }

  downloadUrl(logId: string, format: 'csv' | 'xlsx'): string {
    return `${this.context.apiUrl('privilege-logs', logId, 'content')}?format=${format}`;
  }
}

function toDescription(body: {
  documentVersion: number | string;
  fields: readonly { fieldId: number | string; value?: unknown; editable: boolean }[];
}): Description {
  const field = body.fields.find((f) => Number(f.fieldId) === PRIVILEGE_DESCRIPTION_FIELD);
  return {
    value: typeof field?.value === 'string' ? field.value : '',
    documentVersion: String(body.documentVersion),
    editable: field?.editable ?? false,
  };
}

function splitKey(key: string): [string, string] {
  const i = key.indexOf(':');
  return [key.slice(0, i), key.slice(i + 1)];
}

/** "Document Date on or after 2026-01-15; Log Category: Outside counsel". */
export function ruleConditions(rule: {
  dateField?: string | null;
  onOrAfter?: string | null;
  before?: string | null;
  logCategories: readonly string[];
  attorneysInvolved: readonly string[];
}): string {
  const parts: string[] = [];
  if (rule.onOrAfter || rule.before) {
    parts.push(
      [
        rule.dateField ?? 'Date',
        rule.onOrAfter ? `on or after ${rule.onOrAfter}` : '',
        rule.before ? `before ${rule.before}` : '',
      ]
        .filter(Boolean)
        .join(' '),
    );
  }
  if (rule.logCategories.length) parts.push(`Log Category: ${rule.logCategories.join(', ')}`);
  if (rule.attorneysInvolved.length) {
    parts.push(`Attorneys Involved: ${rule.attorneysInvolved.join(', ')}`);
  }
  return parts.join('; ');
}

export function toVersion(r: PrivilegeLogResource): LogVersion {
  const m = r.metadata;
  return {
    logId: r.logId,
    version: Number(r.version),
    source: r.source,
    productionId: r.productionId ?? null,
    productionName: m.productionName ?? null,
    savedSearchId: r.savedSearchId ?? null,
    savedSearchName: m.savedSearchName ?? null,
    templateId: r.templateId ?? null,
    descriptionColumn: r.descriptionColumn == null ? null : Number(r.descriptionColumn),
    snapshotId: r.snapshotId,
    reviewSetSnapshotId: r.reviewSetSnapshotId ?? null,
    templateName: r.templateName,
    contentSha256: r.contentSha256,
    files: r.files.map((f) => ({
      format: f.format as 'csv' | 'xlsx',
      sha256: f.sha256,
      bytes: Number(f.bytes),
    })),
    entries: Number(m.entries),
    withheld: Number(m.withheld),
    redacted: Number(m.redacted) + Number(m.redactedPrivacy),
    excludedByRules: Number(m.excludedByRules),
    privacyRedactionsIncluded: m.privacyRedactionsIncluded,
    columns: m.columns,
    rules: m.exclusionRules.map((rule) => ({
      label: rule.label,
      conditions: ruleConditions({
        dateField: rule.dateField,
        onOrAfter: rule.onOrAfter as string | null | undefined,
        before: rule.before as string | null | undefined,
        logCategories: rule.logCategories,
        attorneysInvolved: rule.attorneysInvolved,
      }),
      excluded: Number(rule.excludedDocuments),
    })),
    generatedBy: r.generatedBy.displayName,
    generatedAt: String(r.generatedAt),
  };
}
