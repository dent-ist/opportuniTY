import type { Route } from '@playwright/test';

type Actor = { userId: string; displayName: string | null };

/** A `WorkspaceDeletionResource` of the mock. */
export interface MockDeletion {
  deletionId: string;
  workspaceId: string;
  workspaceName: string;
  matterNumber: string | null;
  retentionProfile: 'retainRecords' | 'purgeAll';
  reason: string;
  externalReference: string | null;
  status: string;
  currentStep: string | null;
  requestedBy: Actor;
  requestedAt: string;
  expiresAt: string;
  approvedBy: Actor | null;
  approvedAt: string | null;
  approvalNote: string | null;
  runNotBefore: string | null;
  cancelledBy: Actor | null;
  cancelledAt: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  haltedAt: string | null;
  error: string | null;
  steps: {
    step: string;
    attempt: number;
    startedAt: string;
    finishedAt: string | null;
    outcome: string | null;
    totals: Record<string, number>;
  }[];
  certificateAvailable: boolean;
  canApprove: boolean;
  canCancel: boolean;
  version: number;
}

const ME: Actor = { userId: 'user-1', displayName: 'Alex Reviewer' };
const DANA: Actor = { userId: 'user-7', displayName: 'Dana Counsel' };
const STEPS = [
  'fence',
  'drain',
  'inventory',
  'searchPurge',
  'databasePurge',
  'storagePurge',
  'keyDestruction',
  'verification',
  'certification',
];

const TOTALS: Record<string, Record<string, number>> = {
  inventory: { rows: 48210, documents: 5120, objects: 10240 },
  databasePurge: { rows: 48210 },
  keyDestruction: { destroyed: 2 },
};

function base(extra: Partial<MockDeletion>): MockDeletion {
  return {
    deletionId: 'del-x',
    workspaceId: 'ws-x',
    workspaceName: 'Synthetic Matter',
    matterNumber: null,
    retentionProfile: 'retainRecords',
    reason: 'Matter closed',
    externalReference: null,
    status: 'requested',
    currentStep: null,
    requestedBy: DANA,
    requestedAt: '2026-10-07T09:00:00Z',
    expiresAt: '2026-11-06T09:00:00Z',
    approvedBy: null,
    approvedAt: null,
    approvalNote: null,
    runNotBefore: null,
    cancelledBy: null,
    cancelledAt: null,
    startedAt: null,
    finishedAt: null,
    haltedAt: null,
    error: null,
    steps: [],
    certificateAvailable: false,
    canApprove: false,
    canCancel: false,
    version: 1,
    ...extra,
  };
}

/**
 * Workspace deletions (E20-T02): `…/workspaces/{ws}/deletions` (request, list) and the installation-level
 * `/api/v1/workspace-deletions` (list, one deletion, approve, cancel, certificate). Seeded with a request by Dana
 * waiting for approval (del-7, "Delta Matter") and a finished deletion with its certificate (del-5, "Gamma Archive").
 * `approver` decides what the signed-in user may do; writes are recorded with their body and If-Match.
 */
export class DeletionsMock {
  readonly writes: { path: string; body: Record<string, unknown>; ifMatch: string | null }[] = [];
  private readonly deletions: MockDeletion[] = [
    base({
      deletionId: 'del-7',
      workspaceId: 'ws-7',
      workspaceName: 'Delta Matter',
      matterNumber: 'M-1007',
      reason: 'Settlement final; the protective order requires destruction within 60 days',
      externalReference: 'PO ¶ 14',
    }),
    base({
      deletionId: 'del-5',
      workspaceId: 'ws-5',
      workspaceName: 'Gamma Archive',
      matterNumber: 'M-0905',
      retentionProfile: 'purgeAll',
      status: 'completed',
      requestedAt: '2026-09-01T09:00:00Z',
      approvedBy: ME,
      approvedAt: '2026-09-02T09:00:00Z',
      runNotBefore: '2026-09-09T09:00:00Z',
      startedAt: '2026-09-09T09:00:10Z',
      finishedAt: '2026-09-09T09:12:00Z',
      certificateAvailable: true,
      steps: STEPS.map((step) => ({
        step,
        attempt: 1,
        startedAt: '2026-09-09T09:00:10Z',
        finishedAt: '2026-09-09T09:12:00Z',
        outcome: 'success',
        totals: TOTALS[step] ?? {},
      })),
    }),
  ];
  private next = 10;

  constructor(private readonly approver: () => boolean) {}

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const scoped = /^\/api\/v1\/workspaces\/([^/]+)\/deletions$/.exec(path);
    if (scoped) {
      const ws = scoped[1];
      if (method === 'GET')
        return route.fulfill({
          json: { items: this.view(this.deletions.filter((d) => d.workspaceId === ws)) },
        });
      if (method !== 'POST') return undefined;
      const body = route.request().postDataJSON() as Record<string, unknown>;
      this.writes.push({ path, body, ifMatch: null });
      const created = base({
        deletionId: `del-${this.next++}`,
        workspaceId: ws,
        workspaceName: String(body['confirmName']),
        retentionProfile: body['retentionProfile'] === 'purgeAll' ? 'purgeAll' : 'retainRecords',
        reason: String(body['reason']),
        externalReference: (body['externalReference'] as string | null) ?? null,
        requestedBy: ME,
        requestedAt: '2026-10-09T10:00:00Z',
        expiresAt: '2026-11-08T10:00:00Z',
      });
      this.deletions.unshift(created);
      return route.fulfill({ status: 201, json: this.one(created) });
    }

    const installation = /^\/api\/v1\/workspace-deletions(?:\/([^/]+)(\/[a-z]+)?)?$/.exec(path);
    if (!installation) return undefined;
    const [, id, step] = installation;
    if (!id && method === 'GET')
      return route.fulfill({
        json: {
          items: this.view(
            this.approver()
              ? this.deletions
              : this.deletions.filter((d) => d.requestedBy.userId === ME.userId),
          ),
        },
      });
    const index = this.deletions.findIndex((d) => d.deletionId === id);
    const found = this.deletions[index];
    if (!found || (!this.approver() && found.requestedBy.userId !== ME.userId)) return undefined;
    if (method === 'GET' && !step) return route.fulfill({ json: this.one(found) });
    if (method === 'GET' && step === '/certificate')
      return route.fulfill({
        body: JSON.stringify({
          certificate: { certificateId: id },
          canonical: '{}',
          sha256: '00',
          issuedAt: found.finishedAt,
          signature: null,
        }),
        headers: {
          'content-type': 'application/json',
          'content-disposition': `attachment; filename="destruction-certificate-${id}.json"`,
        },
      });
    if (method !== 'POST') return undefined;
    const body = route.request().postDataJSON() as Record<string, unknown>;
    const ifMatch = route.request().headers()['if-match'] ?? null;
    this.writes.push({ path, body, ifMatch });
    if (ifMatch !== `"${found.version}"`)
      return route.fulfill({
        status: 412,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 412, code: 'version-conflict' }),
      });
    let updated: MockDeletion;
    if (step === '/approve') {
      if (found.requestedBy.userId === ME.userId)
        return route.fulfill({
          status: 403,
          contentType: 'application/problem+json',
          body: JSON.stringify({ status: 403, code: 'second-person-required' }),
        });
      updated = {
        ...found,
        status: 'approved',
        approvedBy: ME,
        approvedAt: '2026-10-09T11:00:00Z',
        approvalNote: (body['note'] as string | null) ?? null,
        runNotBefore: '2026-10-16T11:00:00Z',
      };
    } else if (step === '/cancel') {
      updated = {
        ...found,
        status: 'cancelled',
        cancelledBy: ME,
        cancelledAt: '2026-10-09T11:00:00Z',
        finishedAt: '2026-10-09T11:00:00Z',
      };
    } else return undefined;
    updated.version = found.version + 1;
    this.deletions[index] = updated;
    return route.fulfill({ json: this.one(updated) });
  }

  /** The resource as the API shows it to the signed-in user (what they may do now). */
  private one(d: MockDeletion): MockDeletion {
    const open = d.status === 'requested' || d.status === 'approved';
    return {
      ...d,
      canApprove: this.approver() && d.status === 'requested' && d.requestedBy.userId !== ME.userId,
      canCancel: open && (this.approver() || d.requestedBy.userId === ME.userId),
    };
  }

  private view(list: readonly MockDeletion[]): MockDeletion[] {
    return list.map((d) => this.one(d));
  }
}
