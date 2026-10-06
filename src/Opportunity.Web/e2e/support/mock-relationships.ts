import type { Route } from '@playwright/test';
import type { CodingMock } from './mock-coding';

// The mock API's relationships and coding propagation (wave-12 shared contract "Relationships and propagation", #88
// backend, #136 UI): `GET …/documents/{id}/relationships`, `POST …/coding-propagations/preview` and
// `POST …/coding-propagations`. Families follow the document list of ./mock-api.ts (n % 4 === 3 is the parent of
// n + 1); documents n % 10 === 5 and 6 are duplicates (5 is the Primary); emails (not attachments) form threads of
// up to four. Members the reviewer may not see are omitted entirely (Q-52): no list entry, no count.

const pad = (n: number) => `ACM${String(n).padStart(7, '0')}`;

export const isAttachment = (n: number) => n % 4 === 0;

/** Family members (document numbers) of `n`, parent first; `n` alone when stand-alone. */
export function familyOf(n: number, total: number): number[] {
  const parent = isAttachment(n) ? n - 1 : n;
  return parent % 4 === 3 && parent + 1 <= total ? [parent, parent + 1] : [n];
}

export function duplicatesOf(n: number, total: number): number[] {
  const base = n - (n % 10);
  return n % 10 === 5 || n % 10 === 6 ? [base + 5, base + 6].filter((d) => d <= total) : [n];
}

export function threadOf(n: number, total: number): number[] {
  if (isAttachment(n)) return [];
  const start = n - ((n - 1) % 8);
  return Array.from({ length: 8 }, (_, i) => start + i).filter(
    (d) => d <= total && !isAttachment(d),
  );
}

/** The relationship attributes a search hit carries (wave-12 additions to `SearchHit`). */
export function hitRelations(n: number, total: number) {
  const duplicates = duplicatesOf(n, total);
  const thread = threadOf(n, total);
  return {
    duplicateGroupId: duplicates.length > 1 ? `dup-${duplicates[0]}` : null,
    isDuplicatePrimary: duplicates.length > 1 ? duplicates[0] === n : null,
    emailThreadId: thread.length > 1 ? `thread-${thread[0]}` : null,
  };
}

interface Preview {
  readonly source: number;
  readonly version: number;
  readonly targets: number[];
  readonly fields: number[];
}

/** A propagation request the mock received. */
export interface PropagationRequestLog {
  readonly kind: 'preview' | 'apply';
  readonly body: Record<string, unknown>;
  readonly idempotencyKey: string | null;
}

export class RelationshipsMock {
  readonly requests: PropagationRequestLog[] = [];
  /** Targets above this many run as a job (the API default is 1,000; tests lower it). */
  threshold: number;
  private readonly previews = new Map<string, Preview>();
  private jobs = 0;

  constructor(
    private readonly coding: CodingMock,
    private readonly total: () => number,
    threshold = 1000,
  ) {
    this.threshold = threshold;
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const rel = /^\/api\/v1\/workspaces\/[^/]+\/documents\/doc-(\d+)\/relationships$/.exec(path);
    if (rel && method === 'GET') {
      const fields = (url.searchParams.get('fields') ?? '').split(',').filter(Boolean);
      return route.fulfill({ json: this.relationships(Number(rel[1]), fields) });
    }
    if (method !== 'POST') return undefined;
    if (/^\/api\/v1\/workspaces\/[^/]+\/coding-propagations\/preview$/.test(path)) {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      this.requests.push({ kind: 'preview', body, idempotencyKey: null });
      return route.fulfill({ json: this.preview(body) });
    }
    if (/^\/api\/v1\/workspaces\/[^/]+\/coding-propagations$/.test(path)) {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      const key = route.request().headers()['idempotency-key'] ?? null;
      this.requests.push({ kind: 'apply', body, idempotencyKey: key });
      return this.apply(route, String(body['previewId']));
    }
    return undefined;
  }

  private member(n: number, self: number, fields: readonly string[]) {
    const family = familyOf(n, this.total());
    const duplicates = duplicatesOf(n, this.total());
    return {
      documentId: `doc-${n}`,
      controlNumber: pad(n),
      fileName: isAttachment(n) ? `Attachment ${n}.pdf` : `RE: Quarterly terms ${n}.msg`,
      documentDate: new Date(Date.UTC(2024, 0, 1) + n * 3_600_000).toISOString(),
      familySequence: family.length > 1 ? family.indexOf(n) : null,
      isParent: family.length > 1 && family[0] === n,
      isPrimary: duplicates.length > 1 && duplicates[0] === n,
      isSelf: n === self,
      coding: this.coding.codingOf(n, fields),
    };
  }

  private relationships(n: number, fields: readonly string[]) {
    const total = this.total();
    const family = familyOf(n, total);
    const duplicates = duplicatesOf(n, total);
    const thread = threadOf(n, total);
    const m = (d: number) => this.member(d, n, fields);
    return {
      documentId: `doc-${n}`,
      family: {
        familyId: family.length > 1 ? `doc-${family[0]}` : null,
        parent: family.length > 1 ? m(family[0]) : null,
        members: family.length > 1 ? family.map(m) : [],
      },
      duplicates: {
        duplicateGroupId: duplicates.length > 1 ? `dup-${duplicates[0]}` : null,
        primaryDocumentId: duplicates.length > 1 ? `doc-${duplicates[0]}` : null,
        members: duplicates.length > 1 ? duplicates.map(m) : [],
      },
      thread: {
        emailThreadId: thread.length > 1 ? `thread-${thread[0]}` : null,
        members: thread.length > 1 ? thread.map(m) : [],
        total: thread.length > 1 ? thread.length : 0,
      },
    };
  }

  private preview(body: Record<string, unknown>) {
    const source = Number(String(body['sourceDocumentId']).replace('doc-', ''));
    const scope = String(body['scope']);
    const fields = ((body['fields'] as unknown[]) ?? []).map(Number);
    const total = this.total();
    const targets = new Set<number>();
    if (scope !== 'duplicates') familyOf(source, total).forEach((d) => targets.add(d));
    if (scope !== 'family') duplicatesOf(source, total).forEach((d) => targets.add(d));
    targets.delete(source);
    const conflicts = [...targets].flatMap((d) =>
      fields.flatMap((fieldId) => {
        const current = this.coding.rawValues(d, fieldId);
        const next = this.coding.rawValues(source, fieldId);
        return current.length > 0 && current.join() !== next.join()
          ? [
              {
                documentId: `doc-${d}`,
                controlNumber: pad(d),
                fieldId,
                currentValues: current,
                newValues: next,
              },
            ]
          : [];
      }),
    );
    const previewId = `preview-${this.previews.size + 1}`;
    this.previews.set(previewId, {
      source,
      version: this.coding.versionOf(source),
      targets: [...targets],
      fields,
    });
    return {
      previewId,
      targetCount: targets.size,
      conflictCount: conflicts.length,
      conflicts: conflicts.slice(0, 100),
      skippedCount: 0,
      mode: targets.size > this.threshold ? 'job' : 'interactive',
      threshold: this.threshold,
    };
  }

  private apply(route: Route, previewId: string): Promise<void> {
    const preview = this.previews.get(previewId);
    if (!preview || this.coding.versionOf(preview.source) !== preview.version) {
      return route.fulfill({
        status: 409,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          type: 'urn:opportunity:problem:PREVIEW_STALE',
          title: 'The preview is out of date',
          status: 409,
          code: 'PREVIEW_STALE',
        }),
      });
    }
    if (preview.targets.length > this.threshold) {
      const jobId = `job-propagation-${++this.jobs}`;
      return route.fulfill({
        status: 202,
        json: {
          mode: 'job',
          job: {
            jobId,
            type: 'bulkCoding',
            name: 'Apply to Family',
            status: 'running',
            createdAt: '2026-10-06T10:00:00Z',
            updatedAt: '2026-10-06T10:00:00Z',
          },
        },
      });
    }
    for (const target of preview.targets) {
      for (const fieldId of preview.fields) {
        this.coding.setRaw(target, fieldId, this.coding.storedValue(preview.source, fieldId));
      }
    }
    return route.fulfill({
      json: { mode: 'interactive', applied: preview.targets.length, skipped: 0 },
    });
  }
}
