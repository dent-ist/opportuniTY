import type { Route } from '@playwright/test';

/** A `PreservationLockResource` of the mock. */
export interface MockHold {
  lockId: string;
  status: 'active' | 'releasePending' | 'released';
  scope: 'workspace';
  reason: string;
  matterReference: string | null;
  releaseRequiresApproval: boolean;
  placedBy: { userId: string; displayName: string | null };
  placedAt: string;
  releaseRequestedBy: { userId: string; displayName: string | null } | null;
  releaseRequestedAt: string | null;
  releaseReason: string | null;
  releaseApprovedBy: { userId: string; displayName: string | null } | null;
  releasedAt: string | null;
  version: number;
}

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };
const DANA = { userId: 'user-7', displayName: 'Dana Counsel' };

/**
 * Legal holds (E20-T01): `…/preservation-locks` per workspace. ws-2 starts held (a release Dana requested, waiting for
 * a second person, and a released hold); ws-1 and new workspaces start without holds. Writes are recorded with their
 * body and If-Match; a release by its requester's own approval answers 403 `second-person-required`.
 */
export class LegalHoldsMock {
  readonly writes: { path: string; body: Record<string, unknown>; ifMatch: string | null }[] = [];
  private readonly holds = new Map<string, MockHold[]>([
    [
      'ws-2',
      [
        {
          lockId: 'hold-2',
          status: 'releasePending',
          scope: 'workspace',
          reason: 'Regulator preservation notice of 1 September',
          matterReference: 'REG-2026-17',
          releaseRequiresApproval: true,
          placedBy: DANA,
          placedAt: '2026-09-01T09:00:00Z',
          releaseRequestedBy: DANA,
          releaseRequestedAt: '2026-10-06T15:30:00Z',
          releaseReason: 'Notice withdrawn by the regulator',
          releaseApprovedBy: null,
          releasedAt: null,
          version: 2,
        },
        {
          lockId: 'hold-1',
          status: 'released',
          scope: 'workspace',
          reason: 'Litigation reasonably anticipated',
          matterReference: null,
          releaseRequiresApproval: false,
          placedBy: DANA,
          placedAt: '2026-05-01T09:00:00Z',
          releaseRequestedBy: DANA,
          releaseRequestedAt: '2026-06-01T09:00:00Z',
          releaseReason: 'Claim settled',
          releaseApprovedBy: null,
          releasedAt: '2026-06-01T09:00:00Z',
          version: 2,
        },
      ],
    ],
  ]);
  private next = 10;

  /** Active holds of a workspace (`WorkspaceResource.activePreservationLocks`). */
  activeCount(workspaceId: string): number {
    return (this.holds.get(workspaceId) ?? []).filter((h) => h.status !== 'released').length;
  }

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const match = /^\/api\/v1\/workspaces\/([^/]+)\/preservation-locks(?:\/([^/]+)(\/.*)?)?$/.exec(
      path,
    );
    if (!match) return undefined;
    const [, ws, lockId, step] = match;
    const list = this.holds.get(ws) ?? [];
    this.holds.set(ws, list);
    const body = (method === 'POST' ? route.request().postDataJSON() : {}) as Record<
      string,
      unknown
    >;
    const ifMatch = route.request().headers()['if-match'] ?? null;
    if (method === 'GET' && !lockId)
      return route.fulfill({ json: { items: list, activeCount: this.activeCount(ws) } });
    if (method === 'POST' && !lockId) {
      this.writes.push({ path, body, ifMatch });
      const hold: MockHold = {
        lockId: `hold-${this.next++}`,
        status: 'active',
        scope: 'workspace',
        reason: String(body['reason'] ?? ''),
        matterReference: (body['matterReference'] as string | null) ?? null,
        releaseRequiresApproval: body['releaseRequiresApproval'] !== false,
        placedBy: ME,
        placedAt: '2026-10-08T10:00:00Z',
        releaseRequestedBy: null,
        releaseRequestedAt: null,
        releaseReason: null,
        releaseApprovedBy: null,
        releasedAt: null,
        version: 1,
      };
      list.unshift(hold);
      return route.fulfill({ status: 201, json: hold });
    }
    const index = list.findIndex((h) => h.lockId === lockId);
    if (index < 0 || method !== 'POST') return undefined;
    this.writes.push({ path, body, ifMatch });
    const hold = list[index];
    if (ifMatch !== `"${hold.version}"`)
      return route.fulfill({
        status: 412,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 412, code: 'version-conflict' }),
      });
    let updated: MockHold;
    if (step === '/release') {
      updated = hold.releaseRequiresApproval
        ? {
            ...hold,
            status: 'releasePending',
            releaseRequestedBy: ME,
            releaseRequestedAt: '2026-10-08T11:00:00Z',
            releaseReason: String(body['reason'] ?? ''),
          }
        : {
            ...hold,
            status: 'released',
            releaseRequestedBy: ME,
            releaseRequestedAt: '2026-10-08T11:00:00Z',
            releaseReason: String(body['reason'] ?? ''),
            releasedAt: '2026-10-08T11:00:00Z',
          };
    } else if (step === '/release/approve') {
      if (hold.releaseRequestedBy?.userId === ME.userId)
        return route.fulfill({
          status: 403,
          contentType: 'application/problem+json',
          body: JSON.stringify({ status: 403, code: 'second-person-required' }),
        });
      updated = {
        ...hold,
        status: 'released',
        releaseApprovedBy: ME,
        releasedAt: '2026-10-08T11:00:00Z',
      };
    } else if (step === '/release/cancel') {
      updated = {
        ...hold,
        status: 'active',
        releaseRequestedBy: null,
        releaseRequestedAt: null,
        releaseReason: null,
      };
    } else return undefined;
    updated.version = hold.version + 1;
    list[index] = updated;
    return route.fulfill({ json: updated });
  }
}
