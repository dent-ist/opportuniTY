import { Injectable } from '@angular/core';
import {
  type DocumentRedactions,
  type NormalizedRect,
  type Redaction,
  RedactionApi,
  type RedactionChange,
  RedactionConflictError,
  type RedactionReason,
  type RedactionSetOption,
} from './redaction-api';

export const REASONS: readonly RedactionReason[] = [
  {
    code: 'AttorneyClient',
    name: 'Attorney-Client Privilege',
    category: 'privilege',
    boxLabel: 'Redacted – Privileged',
    active: true,
  },
  { code: 'PII', name: 'PII', category: 'privacy', boxLabel: 'Redacted – PII', active: true },
  {
    code: 'PersonalDataGdpr',
    name: 'Personal Data – GDPR',
    category: 'privacy',
    boxLabel: 'Redacted – Personal Data',
    active: true,
  },
  {
    code: 'TradeSecret',
    name: 'Trade Secret',
    category: 'other',
    boxLabel: 'Redacted',
    active: false,
  },
];

export const SETS: readonly RedactionSetOption[] = [
  { id: 'set-1', name: 'Default', retired: false },
  { id: 'set-2', name: 'Privacy pass', retired: false },
];

export function savedRedaction(
  id: string,
  pageNumber: number,
  rect: NormalizedRect,
  extra: Partial<Redaction> = {},
): Redaction {
  return {
    id,
    pageNumber,
    onActivePageSet: true,
    rect,
    type: 'black',
    reasonCode: 'PII',
    reasonName: 'PII',
    reasonCategory: 'privacy',
    note: null,
    createdBy: { userId: 'other-user', displayName: 'Avery Lee' },
    createdAt: '2026-10-05T09:30:00Z',
    modifiedBy: { userId: 'other-user', displayName: 'Avery Lee' },
    modifiedAt: '2026-10-05T09:30:00Z',
    ...extra,
  };
}

/**
 * The redactions API in memory, versioned like the server: a save with a stale version answers a conflict with the
 * current state and writes nothing. `saves` logs `version:operation(id)…` per accepted or refused save.
 */
@Injectable()
export class FakeRedactionApi extends RedactionApi {
  readonly saves: string[] = [];
  readonly received: { version: number; changes: readonly RedactionChange[] }[] = [];
  redactable = true;
  unavailableReason: string | null = null;
  docs = new Map<string, { version: number; redactions: Redaction[] }>();
  /** Makes the next save look like another user saved first. */
  concurrentEdit: Redaction | null = null;

  async sets(): Promise<readonly RedactionSetOption[]> {
    return SETS;
  }

  async reasons(): Promise<readonly RedactionReason[]> {
    return REASONS;
  }

  async get(documentId: string, setId: string): Promise<DocumentRedactions> {
    return this.state(documentId, setId);
  }

  async save(
    documentId: string,
    setId: string,
    version: number,
    changes: readonly RedactionChange[],
  ): Promise<DocumentRedactions> {
    const doc = this.doc(documentId, setId);
    this.received.push({ version, changes });
    if (this.concurrentEdit) {
      doc.redactions.push(this.concurrentEdit);
      doc.version++;
      this.concurrentEdit = null;
    }
    if (version !== doc.version) {
      this.saves.push(`${version}:conflict`);
      throw new RedactionConflictError(this.state(documentId, setId));
    }
    doc.version++;
    for (const change of changes) {
      const index = doc.redactions.findIndex((r) => r.id === change.redactionId);
      if (change.operation === 'add') {
        doc.redactions.push(
          savedRedaction(change.redactionId, change.pageNumber, change.rect, {
            type: change.type,
            reasonCode: change.reasonCode,
            reasonName:
              REASONS.find((r) => r.code === change.reasonCode)?.name ?? change.reasonCode,
            createdBy: { userId: 'me', displayName: 'Alex Admin' },
          }),
        );
      } else if (change.operation === 'modify') {
        const current = doc.redactions[index];
        doc.redactions[index] = {
          ...current,
          rect: change.rect ?? current.rect,
          type: change.type ?? current.type,
          reasonCode: change.reasonCode ?? current.reasonCode,
          reasonName: REASONS.find((r) => r.code === change.reasonCode)?.name ?? current.reasonName,
        };
      } else {
        doc.redactions.splice(index, 1);
      }
    }
    this.saves.push(
      `${version}:${changes.map((c) => `${c.operation}(${c.redactionId})`).join(',')}`,
    );
    return this.state(documentId, setId);
  }

  private doc(documentId: string, setId: string) {
    const key = `${documentId}/${setId}`;
    let doc = this.docs.get(key);
    if (!doc) {
      doc = { version: 0, redactions: [] };
      this.docs.set(key, doc);
    }
    return doc;
  }

  private state(documentId: string, setId: string): DocumentRedactions {
    const doc = this.doc(documentId, setId);
    return {
      documentId,
      setId,
      version: doc.version,
      redactable: this.redactable,
      unavailableReason: this.redactable
        ? null
        : (this.unavailableReason ?? 'Redaction requires rendered images'),
      setRetired: false,
      lastChange: doc.version ? { displayName: 'Alex Admin', at: '2026-10-06T10:00:00Z' } : null,
      redactions: doc.redactions.map((r) => ({ ...r })),
    };
  }
}
