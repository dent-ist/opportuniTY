import type { Route } from '@playwright/test';

/**
 * Review batches (#178) for the e2e mock API, in the shapes of `GET/POST …/review-batch-sets`, `…/{id}`,
 * `…/{id}/check-out-next`, `…/{id}/assignee-candidates`, `GET …/review-batches` (filters), `…/{id}`, and the status
 * changes `…/check-out`, `…/check-in`, `PUT …/assignment` (If-Match = the batch's version). Batch FP_0001 holds the
 * list's first five documents; a search with `reviewBatchId` lists only a batch's documents (see mock-api.ts).
 */

export interface ReviewBatchRequest {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  ifMatch: string | null;
}

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };
const SAM = { userId: 'user-2', displayName: 'Sam Senior' };

type Status = 'available' | 'checkedOut' | 'completed';

interface Batch {
  batchId: string;
  batchSetId: string;
  batchSetName: string;
  ordinal: number;
  name: string;
  reviewPass: 'firstPass';
  status: Status;
  documentCount: number;
  assignee: { userId: string; displayName: string } | null;
  statusChangedAt: string;
  statusChangedBy: string | null;
  version: number;
}

export class ReviewBatchesMock {
  readonly requests: ReviewBatchRequest[] = [];
  private readonly sets = [
    {
      batchSetId: 'set-1',
      name: 'Smith first pass',
      batchPrefix: 'FP',
      maxBatchSize: 5,
      keepFamiliesTogether: true,
      keepThreadsTogether: false,
      reviewPass: 'firstPass',
      qcOfBatchSetId: null,
      reviewerGroup: null,
      snapshotId: 'snap-rb-1',
      createdBy: SAM,
      createdAt: '2026-10-09T08:00:00Z',
      sort: 'familyDate',
      sortDescending: false,
    },
  ];
  private readonly batches: Batch[] = [
    batch(1, 'available', null, 5),
    batch(2, 'available', null, 5),
    batch(3, 'completed', SAM, 4),
  ];

  /** Documents of a batch for a search bound to it; null for an unknown batch. */
  size(batchId: string): number | null {
    return this.batches.find((b) => b.batchId === batchId)?.documentCount ?? null;
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const m =
      /^\/api\/v1\/workspaces\/[^/]+\/(review-batch-sets|review-batches)(?:\/([^/]+))?(?:\/([a-z-]+))?$/.exec(
        path,
      );
    if (!m) return undefined;
    const [, kind, id, action] = m;
    const request = route.request();
    const body = method === 'GET' ? null : (request.postDataJSON() as Record<string, unknown>);
    this.requests.push({ method, path, body, ifMatch: request.headers()['if-match'] ?? null });
    const json = (value: unknown, status = 200) => route.fulfill({ status, json: value });

    if (kind === 'review-batch-sets') {
      if (!id && method === 'GET') return json(page(this.sets.map((s) => this.setResource(s))));
      if (!id && method === 'POST') {
        const created = {
          ...this.sets[0],
          batchSetId: 'set-2',
          name: String(body?.['name']),
          batchPrefix: String(body?.['batchPrefix']),
          maxBatchSize: Number(body?.['maxBatchSize']),
          snapshotId: String(body?.['snapshotId']),
          sort: String(body?.['sort'] ?? 'controlNumber'),
          createdBy: ME,
        };
        this.sets.unshift(created);
        return json(this.setResource(created), 201);
      }
      const set = this.sets.find((s) => s.batchSetId === id);
      if (!set) return json(notFound(), 404);
      if (!action && method === 'GET') return json(this.setResource(set));
      if (action === 'assignee-candidates' && method === 'GET') return json({ items: [ME, SAM] });
      if (action === 'check-out-next' && method === 'POST') {
        const held = this.batches.find(
          (b) =>
            b.batchSetId === id && b.status === 'checkedOut' && b.assignee?.userId === ME.userId,
        );
        if (held) return json(held);
        const next = this.batches.find((b) => b.batchSetId === id && b.status === 'available');
        if (!next) return json(problem(409, 'No batch of this Batch Set is available.'), 409);
        return json(change(next, 'checkedOut', ME));
      }
      return undefined;
    }

    if (!id && method === 'GET') {
      const status = url.searchParams.get('status');
      const assignee = url.searchParams.get('assigneeId');
      const setId = url.searchParams.get('batchSetId');
      return json(
        page(
          this.batches.filter(
            (b) =>
              (!status || b.status === status) &&
              (!setId || b.batchSetId === setId) &&
              (!assignee || b.assignee?.userId === (assignee === 'me' ? ME.userId : assignee)),
          ),
        ),
      );
    }
    const found = this.batches.find((b) => b.batchId === id);
    if (!found) return json(notFound(), 404);
    if (!action && method === 'GET') return json(found);
    if (request.headers()['if-match'] !== `"${found.version}"`)
      return json(problem(412, 'The batch changed since you read it.'), 412);
    if (action === 'check-out' && method === 'POST') return json(change(found, 'checkedOut', ME));
    if (action === 'check-in' && method === 'POST')
      return json(
        body?.['completed']
          ? change(found, 'completed', found.assignee)
          : change(found, 'available', null),
      );
    if (action === 'assignment' && method === 'PUT') {
      const assignee = [ME, SAM].find((u) => u.userId === body?.['assigneeId']) ?? null;
      return json(change(found, assignee ? 'checkedOut' : 'available', assignee));
    }
    return undefined;
  }

  private setResource(set: (typeof this.sets)[number]) {
    const batches = this.batches.filter((b) => b.batchSetId === set.batchSetId);
    const count = (status: Status) => batches.filter((b) => b.status === status).length;
    return {
      ...set,
      batchCount: batches.length,
      documentCount: batches.reduce((sum, b) => sum + b.documentCount, 0),
      statusCounts: {
        available: count('available'),
        checkedOut: count('checkedOut'),
        completed: count('completed'),
      },
    };
  }
}

function batch(
  ordinal: number,
  status: Status,
  assignee: Batch['assignee'],
  documents: number,
): Batch {
  return {
    batchId: `rb-${ordinal}`,
    batchSetId: 'set-1',
    batchSetName: 'Smith first pass',
    ordinal,
    name: `FP_000${ordinal}`,
    reviewPass: 'firstPass',
    status,
    documentCount: documents,
    assignee,
    statusChangedAt: '2026-10-10T09:00:00Z',
    statusChangedBy: assignee?.userId ?? null,
    version: 1,
  };
}

function change(b: Batch, status: Status, assignee: Batch['assignee']): Batch {
  b.status = status;
  b.assignee = assignee;
  b.statusChangedBy = ME.userId;
  b.statusChangedAt = '2026-10-11T10:00:00Z';
  b.version++;
  return b;
}

function page<T>(items: T[]) {
  return { items, nextCursor: null, total: { value: items.length, relation: 'eq' } };
}

function problem(status: number, detail: string) {
  return { type: 'about:blank', title: 'Conflict', status, detail };
}

function notFound() {
  return { type: 'about:blank', title: 'Not Found', status: 404 };
}
