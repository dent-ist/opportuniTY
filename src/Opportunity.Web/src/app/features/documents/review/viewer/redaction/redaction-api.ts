import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  DocumentRedactionsResource,
  RedactionChangeRequest,
  RedactionReasonList,
  RedactionResource,
  RedactionSetList,
  SaveRedactionsRequest,
} from '../../../../../core/api/generated/models';
import { ApiError } from '../../../../../core/api/problem-details';
import { WorkspaceContext } from '../../../../../core/workspace/workspace-context';

// The redactions API (E11-T04, ADR-012) as the viewer's Redaction mode uses it: Redaction Sets and the reason
// picklist, a document's redactions in one set with their version (ETag), and versioned saves with If-Match.

/** A rectangle in normalized page space: millionths of the page width / height, origin top-left at rotation 0. */
export interface NormalizedRect {
  readonly x: number;
  readonly y: number;
  readonly w: number;
  readonly h: number;
}

export const NORMALIZED_SCALE = 1_000_000;

export type RedactionType = 'black' | 'labelled';

export type ReasonCategory = 'privilege' | 'privacy' | 'other';

export interface Redaction {
  readonly id: string;
  readonly pageNumber: number;
  /** False when the document's page images were replaced since it was drawn: review it. */
  readonly onActivePageSet: boolean;
  readonly rect: NormalizedRect;
  readonly type: RedactionType;
  readonly reasonCode: string;
  readonly reasonName: string;
  readonly reasonCategory: ReasonCategory;
  readonly note: string | null;
  readonly createdBy: { readonly userId: string; readonly displayName: string };
  readonly createdAt: string;
  readonly modifiedBy: { readonly userId: string; readonly displayName: string };
  readonly modifiedAt: string;
}

/** A document's redactions in one Redaction Set. */
export interface DocumentRedactions {
  readonly documentId: string;
  readonly setId: string;
  /** The version, sent back as `If-Match`. */
  readonly version: number;
  /** New redactions can be drawn (the page images are rendered). */
  readonly redactable: boolean;
  /** Why not ("Redaction requires rendered images"); null when redactable. */
  readonly unavailableReason: string | null;
  readonly setRetired: boolean;
  readonly lastChange: { readonly displayName: string; readonly at: string } | null;
  readonly redactions: readonly Redaction[];
}

export interface RedactionSetOption {
  readonly id: string;
  readonly name: string;
  readonly retired: boolean;
}

export interface RedactionReason {
  readonly code: string;
  readonly name: string;
  readonly category: ReasonCategory;
  readonly boxLabel: string;
  readonly active: boolean;
}

/** One change of a save; all changes of a save become one new version. */
export type RedactionChange =
  | {
      readonly operation: 'add';
      readonly redactionId: string;
      readonly pageNumber: number;
      readonly rect: NormalizedRect;
      readonly type: RedactionType;
      readonly reasonCode: string;
    }
  | {
      readonly operation: 'modify';
      readonly redactionId: string;
      readonly rect?: NormalizedRect;
      readonly type?: RedactionType;
      readonly reasonCode?: string;
    }
  | { readonly operation: 'remove'; readonly redactionId: string };

/** 412: another user saved first. `current` is what is saved now; nothing of the refused save was written. */
export class RedactionConflictError extends Error {
  constructor(readonly current: DocumentRedactions) {
    super('Another user changed the redactions of this document.');
    this.name = 'RedactionConflictError';
  }
}

@Injectable()
export abstract class RedactionApi {
  /** The workspace's Redaction Sets, active ones first. */
  abstract sets(): Promise<readonly RedactionSetOption[]>;
  /** The reason picklist in display order. */
  abstract reasons(): Promise<readonly RedactionReason[]>;
  abstract get(documentId: string, setId: string): Promise<DocumentRedactions>;
  /** Rejects with `RedactionConflictError` when `version` is stale; never overwrites. */
  abstract save(
    documentId: string,
    setId: string,
    version: number,
    changes: readonly RedactionChange[],
  ): Promise<DocumentRedactions>;
}

/** `…/redaction-sets`, `…/redaction-reasons` and `…/documents/{id}/redaction-sets/{setId}` (ADR-019). */
@Injectable()
export class HttpRedactionApi extends RedactionApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async sets(): Promise<readonly RedactionSetOption[]> {
    const body = await firstValueFrom(
      this.http.get<RedactionSetList>(this.context.apiUrl('redaction-sets')),
    );
    return body.items.map((s) => ({ id: s.redactionSetId, name: s.name, retired: s.retired }));
  }

  async reasons(): Promise<readonly RedactionReason[]> {
    const body = await firstValueFrom(
      this.http.get<RedactionReasonList>(this.context.apiUrl('redaction-reasons')),
    );
    return body.items.map((r) => ({
      code: r.code,
      name: r.name,
      category: r.category,
      boxLabel: r.boxLabel,
      active: r.active,
    }));
  }

  async get(documentId: string, setId: string): Promise<DocumentRedactions> {
    const body = await firstValueFrom(
      this.http.get<DocumentRedactionsResource>(
        this.context.apiUrl('documents', documentId, 'redaction-sets', setId),
      ),
    );
    return toDocumentRedactions(body);
  }

  async save(
    documentId: string,
    setId: string,
    version: number,
    changes: readonly RedactionChange[],
  ): Promise<DocumentRedactions> {
    const request: SaveRedactionsRequest = {
      changes: changes.map((c) => ({ ...c }) as RedactionChangeRequest),
    };
    try {
      const body = await firstValueFrom(
        this.http.post<DocumentRedactionsResource>(
          this.context.apiUrl('documents', documentId, 'redaction-sets', setId, 'revisions'),
          request,
          { headers: { 'If-Match': `"${version}"` } },
        ),
      );
      return toDocumentRedactions(body);
    } catch (e) {
      if (e instanceof ApiError && e.status === 412 && e.problem['current']) {
        throw new RedactionConflictError(
          toDocumentRedactions(e.problem['current'] as DocumentRedactionsResource),
        );
      }
      throw e;
    }
  }
}

export function toDocumentRedactions(body: DocumentRedactionsResource): DocumentRedactions {
  return {
    documentId: body.documentId,
    setId: body.redactionSetId,
    version: Number(body.currentVersion),
    redactable: body.redactable,
    unavailableReason: body.unavailableReason,
    setRetired: body.setRetired,
    lastChange: body.lastChange
      ? { displayName: body.lastChange.actor.displayName, at: String(body.lastChange.at) }
      : null,
    redactions: body.redactions.map(toRedaction),
  };
}

function toRedaction(r: RedactionResource): Redaction {
  return {
    id: r.redactionId,
    pageNumber: Number(r.pageNumber),
    onActivePageSet: r.onActivePageSet,
    rect: { x: Number(r.rect.x), y: Number(r.rect.y), w: Number(r.rect.w), h: Number(r.rect.h) },
    type: r.type,
    reasonCode: r.reasonCode,
    reasonName: r.reasonName,
    reasonCategory: r.reasonCategory,
    note: r.note,
    createdBy: r.createdBy,
    createdAt: String(r.createdAt),
    modifiedBy: r.modifiedBy,
    modifiedAt: String(r.modifiedAt),
  };
}
