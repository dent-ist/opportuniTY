import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { listFields } from '../../../core/api/generated/fn/fields/list-fields';
import type { FieldResource } from '../../../core/api/generated/models';
import { ApiError } from '../../../core/api/problem-details';
import { type JobSummary, toJobSummary } from '../../../core/jobs/job-model';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// Relationships and coding propagation (E09-T05 backend #88, UI #136) as a port. Written by hand against the wave-12
// shared contract "Relationships and propagation" while the API is built in parallel; the e2e mock API
// (e2e/support/mock-relationships.ts) serves the same shapes. Readers are tolerant (int64 as strings, missing fields).
// The port speaks field query names; the adapter translates to the field ids the propagation API takes.

/** A family, duplicate or thread member the caller may see (members they may not see are only counted). */
export interface RelatedDocument {
  readonly documentId: string;
  readonly controlNumber: string;
  readonly fileName: string | null;
  readonly documentDate: string | null;
  readonly familySequence: number | null;
  readonly isParent: boolean;
  /** The primary of its duplicate group (Q-09: "Primary", never "master"). */
  readonly isPrimary: boolean;
  /** The document the relationships were asked for. */
  readonly isSelf: boolean;
  /** Current values of the asked-for coding fields by query name (choices as names). */
  readonly coding: Readonly<Record<string, readonly string[]>>;
}

export interface FamilyRelations {
  readonly familyId: string | null;
  readonly parent: RelatedDocument | null;
  /** In family order (Family Sequence), including the document itself. */
  readonly members: readonly RelatedDocument[];
}

export interface DuplicateRelations {
  readonly duplicateGroupId: string | null;
  readonly primaryDocumentId: string | null;
  readonly members: readonly RelatedDocument[];
}

export interface ThreadRelations {
  readonly emailThreadId: string | null;
  /** The first 200 members. */
  readonly members: readonly RelatedDocument[];
  readonly total: number;
}

/** `GET …/documents/{id}/relationships`. */
export interface DocumentRelationships {
  readonly documentId: string;
  readonly family: FamilyRelations;
  readonly duplicates: DuplicateRelations;
  readonly thread: ThreadRelations;
}

/** At most this many coding fields per relationships request (contract). */
export const MAX_RELATIONSHIP_FIELDS = 20;

export type PropagationScope = 'family' | 'duplicates' | 'familyAndDuplicates';

export interface PropagationRequest {
  readonly sourceDocumentId: string;
  readonly scope: PropagationScope;
  /** Field query names; the values are the source document's current (saved) coding. */
  readonly fields: readonly string[];
}

/** A target whose current, non-empty value differs from the one being applied. */
export interface PropagationConflict {
  readonly documentId: string;
  readonly controlNumber: string;
  /** Query name of the field (the field id when the catalogue does not know it). */
  readonly field: string;
  readonly currentValues: readonly string[];
  readonly newValues: readonly string[];
}

/** `POST …/coding-propagations/preview`. */
export interface PropagationPreview {
  readonly previewId: string;
  readonly targetCount: number;
  readonly conflictCount: number;
  /** The first 100 conflicts. */
  readonly conflicts: readonly PropagationConflict[];
  readonly skippedCount: number;
  /** `job` above `threshold` targets (Q-14: a Mass Edit job, Q-07 skip rule). */
  readonly mode: 'interactive' | 'job';
  readonly threshold: number;
}

/** `POST …/coding-propagations`: applied at once (200) or started as a job (202). */
export type PropagationResult =
  | { readonly mode: 'interactive'; readonly applied: number; readonly skipped: number }
  | { readonly mode: 'job'; readonly job: JobSummary };

/** 409 `PREVIEW_STALE`: the preview expired (10 minutes) or the source coding changed since. */
export class PropagationStaleError extends Error {
  constructor() {
    super('The preview is out of date.');
    this.name = 'PropagationStaleError';
  }
}

@Injectable()
export abstract class RelationshipsApi {
  /** The family, duplicates and email thread of a document, with the coding of `fields` (query names, ≤ 20). */
  abstract relationships(
    documentId: string,
    fields: readonly string[],
  ): Promise<DocumentRelationships>;
  /** Counts and conflicts of applying the source document's coding of `fields` to its family or duplicates. */
  abstract preview(request: PropagationRequest): Promise<PropagationPreview>;
  /** Applies a preview; rejects with `PropagationStaleError` when it is out of date. */
  abstract apply(previewId: string, idempotencyKey: string): Promise<PropagationResult>;
}

@Injectable()
export class HttpRelationshipsApi extends RelationshipsApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);
  private catalog?: Promise<readonly FieldResource[]>;

  relationships(documentId: string, fields: readonly string[]): Promise<DocumentRelationships> {
    const names = fields.slice(0, MAX_RELATIONSHIP_FIELDS);
    const params: Record<string, string> = names.length ? { fields: names.join(',') } : {};
    return firstValueFrom(
      this.http.get<unknown>(this.context.apiUrl('documents', documentId, 'relationships'), {
        params,
      }),
    ).then(toRelationships);
  }

  async preview(request: PropagationRequest): Promise<PropagationPreview> {
    const fields = await this.fields();
    const byName = new Map(fields.map((f) => [f.queryName.toLowerCase(), String(f.fieldId)]));
    const byId = new Map(fields.map((f) => [String(f.fieldId), f]));
    const body = {
      sourceDocumentId: request.sourceDocumentId,
      scope: request.scope,
      // Field ids as JSON numbers when they are numeric (the API's canonical ids).
      fields: request.fields.map((q) => idValue(byName.get(q.toLowerCase()) ?? q)),
    };
    const raw = await firstValueFrom(
      this.http.post<unknown>(this.context.apiUrl('coding-propagations', 'preview'), body),
    );
    return toPreview(
      raw,
      (id) => byId.get(id)?.queryName ?? id,
      (id, value) => {
        // Choice values may come as choice ids: show their names.
        const choice = byId.get(id)?.choices?.find((c) => String(c.choiceId) === value);
        return choice?.name ?? value;
      },
    );
  }

  async apply(previewId: string, idempotencyKey: string): Promise<PropagationResult> {
    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(
          this.context.apiUrl('coding-propagations'),
          { previewId },
          { headers: { 'Idempotency-Key': idempotencyKey } },
        ),
      );
      return toResult(raw);
    } catch (e) {
      if (e instanceof ApiError && isStale(e)) throw new PropagationStaleError();
      throw e;
    }
  }

  private fields(): Promise<readonly FieldResource[]> {
    this.catalog ??= firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    ).then(
      (r) => r.body.items,
      () => {
        this.catalog = undefined;
        return [];
      },
    );
    return this.catalog;
  }
}

/** 409 with code `PREVIEW_STALE` (any spelling: `preview-stale`, `previewStale`). */
export function isStale(error: ApiError): boolean {
  if (error.status !== 409) return false;
  const code = error.code.toLowerCase().replace(/[^a-z]/g, '');
  return code === 'previewstale' || code === 'http409';
}

// ── Tolerant readers ────────────────────────────────────────────────────────────────────────────────────────

type Raw = Record<string, unknown>;
const obj = (v: unknown): Raw => (v && typeof v === 'object' ? (v as Raw) : {});
const arr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
const str = (v: unknown, fallback = ''): string =>
  v === null || v === undefined ? fallback : String(v);
const strOrNull = (v: unknown): string | null =>
  v === null || v === undefined || v === '' ? null : String(v);
const num = (v: unknown): number => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};
const numOrNull = (v: unknown): number | null =>
  v === null || v === undefined || v === '' || !Number.isFinite(Number(v)) ? null : Number(v);
const idValue = (id: string): string | number => (/^\d+$/.test(id) ? Number(id) : id);

export function toRelatedDocument(v: unknown): RelatedDocument {
  const raw = obj(v);
  const coding: Record<string, string[]> = {};
  for (const [key, values] of Object.entries(obj(raw['coding']))) {
    coding[key.toLowerCase()] = arr(values).map((x) => str(x));
  }
  return {
    documentId: str(raw['documentId']),
    controlNumber: str(raw['controlNumber']),
    fileName: strOrNull(raw['fileName']),
    documentDate: strOrNull(raw['documentDate']),
    familySequence: numOrNull(raw['familySequence']),
    isParent: raw['isParent'] === true,
    isPrimary: raw['isPrimary'] === true,
    isSelf: raw['isSelf'] === true,
    coding,
  };
}

export function toRelationships(v: unknown): DocumentRelationships {
  const raw = obj(v);
  const family = obj(raw['family']);
  const duplicates = obj(raw['duplicates']);
  const thread = obj(raw['thread']);
  const threadMembers = arr(thread['members']).map(toRelatedDocument);
  return {
    documentId: str(raw['documentId']),
    family: {
      familyId: strOrNull(family['familyId']),
      parent: family['parent'] ? toRelatedDocument(family['parent']) : null,
      members: arr(family['members']).map(toRelatedDocument),
    },
    duplicates: {
      duplicateGroupId: strOrNull(duplicates['duplicateGroupId']),
      primaryDocumentId: strOrNull(duplicates['primaryDocumentId']),
      members: arr(duplicates['members']).map(toRelatedDocument),
    },
    thread: {
      emailThreadId: strOrNull(thread['emailThreadId']),
      members: threadMembers,
      total: Math.max(num(thread['total']), threadMembers.length),
    },
  };
}

export function toPreview(
  v: unknown,
  fieldName: (id: string) => string = (id) => id,
  valueName: (fieldId: string, value: string) => string = (_, value) => value,
): PropagationPreview {
  const raw = obj(v);
  const conflicts = arr(raw['conflicts']).map((c): PropagationConflict => {
    const conflict = obj(c);
    const fieldId = str(conflict['fieldId']);
    const values = (x: unknown) => arr(x).map((value) => valueName(fieldId, str(value)));
    return {
      documentId: str(conflict['documentId']),
      controlNumber: str(conflict['controlNumber']),
      field: fieldName(fieldId),
      currentValues: values(conflict['currentValues']),
      newValues: values(conflict['newValues']),
    };
  });
  return {
    previewId: str(raw['previewId']),
    targetCount: num(raw['targetCount']),
    conflictCount: Math.max(num(raw['conflictCount']), conflicts.length),
    conflicts,
    skippedCount: num(raw['skippedCount']),
    mode: raw['mode'] === 'job' ? 'job' : 'interactive',
    threshold: num(raw['threshold']) || 1000,
  };
}

export function toResult(v: unknown): PropagationResult {
  const raw = obj(v);
  if (raw['mode'] === 'job' || raw['job']) return { mode: 'job', job: toJobSummary(raw['job']) };
  return { mode: 'interactive', applied: num(raw['applied']), skipped: num(raw['skipped']) };
}
