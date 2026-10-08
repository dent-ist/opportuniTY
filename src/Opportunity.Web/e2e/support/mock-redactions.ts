import type { Route } from '@playwright/test';

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface StoredRedaction {
  redactionId: string;
  pageSetId: string;
  pageNumber: number;
  onActivePageSet: boolean;
  rect: Rect;
  type: 'black' | 'labelled';
  reasonCode: string;
  reasonName: string;
  reasonCategory: string;
  note: string | null;
  createdBy: { userId: string; displayName: string };
  createdAt: string;
  modifiedBy: { userId: string; displayName: string };
  modifiedAt: string;
  changedAtVersion: number;
}

const REASONS = [
  ['AttorneyClient', 'Attorney-Client Privilege', 'privilege', 'Redacted – Privileged'],
  ['WorkProduct', 'Work Product', 'privilege', 'Redacted – Work Product'],
  ['PII', 'PII', 'privacy', 'Redacted – PII'],
  ['PHI', 'PHI', 'privacy', 'Redacted – PHI'],
  ['PersonalDataGdpr', 'Personal Data – GDPR', 'privacy', 'Redacted – Personal Data'],
  ['Other', 'Other', 'other', 'Redacted'],
].map(([code, name, category, boxLabel], i) => ({
  code,
  name,
  category,
  boxLabel,
  active: true,
  sortOrder: (i + 1) * 10,
  version: 1,
}));

const ME = { userId: 'user-1', displayName: 'Alex Admin' };
const OTHER = { userId: 'user-2', displayName: 'Avery Lee' };

/**
 * The redactions API (E11-T04) as the server answers it: Redaction Sets and reasons, and per document and set a
 * versioned list of rectangles. A save with a stale If-Match answers 412 with the current state and writes nothing.
 * Documents with page images (number % 10 === 4) are redactable; every other one says "Redaction requires rendered
 * images".
 */
export class RedactionsMock {
  private readonly docs = new Map<string, { version: number; redactions: StoredRedaction[] }>();
  /** Every accepted save, as `documentId v<version>: operation …`. */
  readonly saves: string[] = [];

  /** Another user adds a redaction to `doc-<n>` (the reviewer's next save then conflicts). */
  concurrentEdit(n: number, pageNumber = 1): void {
    const doc = this.doc(`doc-${n}`, 'rs-1');
    doc.version++;
    doc.redactions.push(
      this.redaction(
        `theirs-${doc.version}`,
        pageNumber,
        { x: 100_000, y: 800_000, w: 300_000, h: 50_000 },
        'PII',
        'black',
        OTHER,
        doc.version,
      ),
    );
  }

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    if (/^\/api\/v1\/workspaces\/[^/]+\/redaction-sets$/.test(path) && method === 'GET') {
      return route.fulfill({
        json: {
          items: [
            {
              redactionSetId: 'rs-1',
              name: 'Default',
              description: null,
              retired: false,
              modifiedBy: null,
              modifiedAt: '2026-10-01T09:00:00Z',
              version: 1,
            },
            {
              redactionSetId: 'rs-2',
              name: 'Privacy pass',
              description: null,
              retired: false,
              modifiedBy: null,
              modifiedAt: '2026-10-01T09:00:00Z',
              version: 1,
            },
          ],
        },
      });
    }
    if (/^\/api\/v1\/workspaces\/[^/]+\/redaction-reasons$/.test(path) && method === 'GET') {
      return route.fulfill({ json: { items: REASONS } });
    }
    const doc =
      /^\/api\/v1\/workspaces\/[^/]+\/documents\/(doc-(\d+))\/redaction-sets\/([^/]+)(\/revisions)?$/.exec(
        path,
      );
    if (!doc) return undefined;
    const [, documentId, n, setId, revisions] = doc;
    if (!revisions && method === 'GET') {
      return route.fulfill({
        json: this.state(documentId, Number(n), setId),
        headers: { ETag: `"${this.doc(documentId, setId).version}"` },
      });
    }
    if (revisions && method === 'POST') return this.save(route, documentId, Number(n), setId);
    return undefined;
  }

  private save(route: Route, documentId: string, n: number, setId: string): Promise<void> {
    const ifMatch = route.request().headers()['if-match'];
    const doc = this.doc(documentId, setId);
    if (!ifMatch) {
      return route.fulfill({
        status: 428,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 428, code: 'precondition-required' }),
      });
    }
    if (ifMatch !== `"${doc.version}"`) {
      const current = this.state(documentId, n, setId);
      return route.fulfill({
        status: 412,
        contentType: 'application/problem+json',
        headers: { ETag: `"${doc.version}"` },
        body: JSON.stringify({
          status: 412,
          code: 'version-conflict',
          title: 'Precondition Failed',
          detail: "Another user changed this document's redactions since you read them.",
          currentVersion: doc.version,
          lastChange: current.lastChange,
          current,
        }),
      });
    }
    if (n % 10 !== 4) {
      return route.fulfill({
        status: 409,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          status: 409,
          code: 'redaction-requires-images',
          detail: 'Redaction requires rendered images.',
        }),
      });
    }
    const body = route.request().postDataJSON() as { changes: Record<string, unknown>[] };
    doc.version++;
    for (const change of body.changes) {
      const id = String(change['redactionId']);
      const index = doc.redactions.findIndex((r) => r.redactionId === id);
      if (change['operation'] === 'add') {
        doc.redactions.push(
          this.redaction(
            id,
            Number(change['pageNumber']),
            change['rect'] as Rect,
            String(change['reasonCode']),
            change['type'] as 'black' | 'labelled',
            ME,
            doc.version,
          ),
        );
      } else if (change['operation'] === 'modify' && index >= 0) {
        const current = doc.redactions[index];
        const reasonCode = (change['reasonCode'] as string | undefined) ?? current.reasonCode;
        const reason = REASONS.find((r) => r.code === reasonCode)!;
        doc.redactions[index] = {
          ...current,
          rect: (change['rect'] as Rect | undefined) ?? current.rect,
          type: (change['type'] as 'black' | 'labelled' | undefined) ?? current.type,
          reasonCode,
          reasonName: reason.name,
          reasonCategory: reason.category,
          modifiedBy: ME,
          changedAtVersion: doc.version,
        };
      } else if (change['operation'] === 'remove' && index >= 0) {
        doc.redactions.splice(index, 1);
      }
    }
    this.saves.push(
      `${documentId} v${doc.version}: ${body.changes.map((c) => `${c['operation']} ${JSON.stringify(c['rect'] ?? '')}`).join(', ')}`,
    );
    return route.fulfill({
      json: this.state(documentId, n, setId),
      headers: { ETag: `"${doc.version}"` },
    });
  }

  private redaction(
    id: string,
    pageNumber: number,
    rect: Rect,
    reasonCode: string,
    type: 'black' | 'labelled',
    actor: { userId: string; displayName: string },
    version: number,
  ): StoredRedaction {
    const reason = REASONS.find((r) => r.code === reasonCode)!;
    return {
      redactionId: id,
      pageSetId: 'ps-1',
      pageNumber,
      onActivePageSet: true,
      rect,
      type,
      reasonCode,
      reasonName: reason.name,
      reasonCategory: reason.category,
      note: null,
      createdBy: actor,
      createdAt: '2026-10-06T10:00:00Z',
      modifiedBy: actor,
      modifiedAt: '2026-10-06T10:00:00Z',
      changedAtVersion: version,
    };
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

  private state(documentId: string, n: number, setId: string) {
    const doc = this.doc(documentId, setId);
    const redactable = n % 10 === 4;
    return {
      documentId,
      redactionSetId: setId,
      version: doc.version,
      currentVersion: doc.version,
      activePageSetId: redactable ? 'ps-1' : null,
      redactable,
      unavailableReason: redactable ? null : 'Redaction requires rendered images',
      setRetired: false,
      lastChange: doc.version
        ? {
            actor: doc.redactions.at(-1)?.modifiedBy ?? ME,
            at: '2026-10-06T10:00:00Z',
            version: doc.version,
          }
        : null,
      redactions: doc.redactions,
    };
  }
}
