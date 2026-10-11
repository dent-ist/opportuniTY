import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { generatePrivilegeLog } from '../../../core/api/generated/fn/privilege-logs/generate-privilege-log';
import { listPrivilegeLogTemplates } from '../../../core/api/generated/fn/privilege-logs/list-privilege-log-templates';
import { listPrivilegeLogs } from '../../../core/api/generated/fn/privilege-logs/list-privilege-logs';
import { listProductions } from '../../../core/api/generated/fn/productions/list-productions';
import type { PrivilegeLogResource } from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// Privilege logs (E13-T03) as a port over the generated client: finalized productions to log, templates and presets,
// generation, versions, and the CSV/XLSX downloads through the protected-content gateway. Provided by the page, so it
// is workspace-scoped.

export type LogPreset = 'documentByDocument' | 'metadataOnly';

/** A template or a built-in preset, as the generator chooses it. */
export interface LogTemplateChoice {
  /** `preset:<name>` or `template:<id>`. */
  readonly key: string;
  readonly name: string;
  readonly columns: readonly string[];
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
  readonly productionId: string | null;
  readonly productionName: string | null;
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
  /** `GET …/privilege-logs` newest first; only versions the caller may read. */
  abstract versions(): Promise<readonly LogVersion[]>;
  /** `POST …/privilege-logs`: a new version, or the unchanged latest one. */
  abstract generate(productionId: string, templateKey: string): Promise<Generation>;
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

  async versions(): Promise<readonly LogVersion[]> {
    const response = await firstValueFrom(
      listPrivilegeLogs(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        limit: 100,
      }),
    );
    return response.body.items.map(toVersion);
  }

  generate(productionId: string, templateKey: string): Promise<Generation> {
    const [kind, value] = splitKey(templateKey);
    return firstValueFrom(
      generatePrivilegeLog(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        body: {
          productionId,
          ...(kind === 'template' ? { templateId: value } : { preset: value as LogPreset }),
        },
      }).pipe(map((r) => ({ log: toVersion(r.body.log), unchanged: r.body.unchanged }))),
    );
  }

  downloadUrl(logId: string, format: 'csv' | 'xlsx'): string {
    return `${this.context.apiUrl('privilege-logs', logId, 'content')}?format=${format}`;
  }
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
    productionId: r.productionId ?? null,
    productionName: m.productionName ?? null,
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
