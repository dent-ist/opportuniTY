import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { listFields } from '../../../core/api/generated/fn/fields/list-fields';
import { getPrivilegeConflicts } from '../../../core/api/generated/fn/privilege/get-privilege-conflicts';
import { propagatePrivilegeCalls } from '../../../core/api/generated/fn/privilege/propagate-privilege-calls';
import type {
  PrivilegeCodedValueResource,
  PrivilegeConflictGroupResource,
  PrivilegeConflictMemberResource,
  PrivilegeConflictReportResource,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// Privilege conflicts (E13-T02) as a port over the generated client: the on-demand report, its CSV through the
// protected-content gateway, and "propagate privilege call to duplicates" (one bulk coding job). Provided by the page,
// so it is workspace-scoped.

export type ConflictKind = 'family' | 'duplicates';
export type ConflictReason = 'withheldMember' | 'responsivenessDiffers' | 'privilegeCallsDiffer';

export interface Reviewer {
  readonly userId: string;
  readonly displayName: string;
}

/** A field's value on a document (choice names) and who last set or cleared it. */
export interface CodedValue {
  readonly values: readonly string[];
  readonly changedBy: Reviewer | null;
  readonly changedAt: string | null;
}

export interface ConflictMember {
  readonly documentId: string;
  readonly controlNumber: string;
  readonly familySequence: number;
  readonly isPrimary: boolean;
  readonly inProduction: boolean;
  readonly status: CodedValue;
  readonly basis: CodedValue;
  readonly responsiveness: CodedValue | null;
}

export interface ConflictGroup {
  readonly kind: ConflictKind;
  readonly groupId: string;
  readonly reasons: readonly ConflictReason[];
  readonly members: readonly ConflictMember[];
}

export interface ConflictReport {
  readonly generatedAt: string;
  readonly responsivenessFieldId: number | null;
  readonly familyConflictCount: number;
  readonly duplicateConflictCount: number;
  readonly truncated: boolean;
  readonly groups: readonly ConflictGroup[];
}

/** A single-choice coding field that can serve as the responsiveness call. */
export interface ResponsivenessField {
  readonly fieldId: number;
  readonly name: string;
}

export interface PropagationGroup {
  readonly duplicateGroupId: string;
  readonly sourceDocumentId: string;
}

/** The job a propagation started. */
export interface PropagationJob {
  readonly jobId: string;
}

@Injectable()
export abstract class PrivilegeConflictApi {
  /** `GET …/privilege-conflicts?responsivenessField=`: computed on request; only documents the caller may see. */
  abstract report(responsivenessFieldId: number | null): Promise<ConflictReport>;
  /** Single-choice coding fields (`GET …/fields`), the privilege fields excluded. */
  abstract responsivenessFields(): Promise<readonly ResponsivenessField[]>;
  /** `POST …/privilege-conflicts/propagations` with Idempotency-Key: 202 with the bulk coding job. */
  abstract propagate(
    groups: readonly PropagationGroup[],
    idempotencyKey: string,
  ): Promise<PropagationJob>;
  /** `GET …/privilege-conflicts/export` (protected-content gateway download, audited). */
  abstract exportUrl(responsivenessFieldId: number | null): string;
}

/** The privilege system fields (ids 37–41, E13-T01): never offered as a responsiveness field. */
const PRIVILEGE_FIELD_IDS = new Set([37, 38, 39, 40, 41]);

@Injectable()
export class HttpPrivilegeConflictApi extends PrivilegeConflictApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  report(responsivenessFieldId: number | null): Promise<ConflictReport> {
    return firstValueFrom(
      getPrivilegeConflicts(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        ...(responsivenessFieldId === null ? {} : { responsivenessField: responsivenessFieldId }),
      }).pipe(map((r) => toReport(r.body))),
    );
  }

  async responsivenessFields(): Promise<readonly ResponsivenessField[]> {
    const response = await firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    );
    return response.body.items
      .filter((f) => f.type === 'singleChoice' && f.storage === 'coding')
      .map((f) => ({ fieldId: Number(f.fieldId), name: f.displayName }))
      .filter((f) => !PRIVILEGE_FIELD_IDS.has(f.fieldId));
  }

  propagate(groups: readonly PropagationGroup[], idempotencyKey: string): Promise<PropagationJob> {
    return firstValueFrom(
      propagatePrivilegeCalls(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        'Idempotency-Key': idempotencyKey,
        body: { groups: groups.map((g) => ({ ...g })) },
      }).pipe(map((r) => ({ jobId: String(r.body.jobId) }))),
    );
  }

  exportUrl(responsivenessFieldId: number | null): string {
    const base = this.context.apiUrl('privilege-conflicts', 'export');
    return responsivenessFieldId === null
      ? base
      : `${base}?responsivenessField=${responsivenessFieldId}`;
  }
}

function toValue(v: PrivilegeCodedValueResource | null | undefined): CodedValue {
  return {
    values: v?.values ?? [],
    changedBy: v?.changedBy
      ? { userId: v.changedBy.userId, displayName: v.changedBy.displayName }
      : null,
    changedAt: (v?.changedAt as string | null | undefined) ?? null,
  };
}

function toMember(m: PrivilegeConflictMemberResource): ConflictMember {
  return {
    documentId: m.documentId,
    controlNumber: m.controlNumber,
    familySequence: Number(m.familySequence),
    isPrimary: m.isPrimary,
    inProduction: m.inProduction,
    status: toValue(m.privilegeStatus),
    basis: toValue(m.privilegeBasis),
    responsiveness: m.responsiveness ? toValue(m.responsiveness) : null,
  };
}

function toGroup(g: PrivilegeConflictGroupResource): ConflictGroup {
  return {
    kind: g.kind as ConflictKind,
    groupId: g.groupId,
    reasons: g.reasons as ConflictReason[],
    members: g.members.map(toMember),
  };
}

export function toReport(r: PrivilegeConflictReportResource): ConflictReport {
  return {
    generatedAt: String(r.generatedAt),
    responsivenessFieldId:
      r.responsivenessFieldId === null || r.responsivenessFieldId === undefined
        ? null
        : Number(r.responsivenessFieldId),
    familyConflictCount: Number(r.familyConflictCount),
    duplicateConflictCount: Number(r.duplicateConflictCount),
    truncated: r.truncated,
    groups: r.groups.map(toGroup),
  };
}
