import type { Route } from '@playwright/test';

const WALL = '0199c3a0-0000-7000-8000-00000000a001';
const DOC = '0199c3a0-0000-7000-8000-00000000d001';

/**
 * Compliance reports (E14-T06): ws-1 has one ethical wall ("Northwind conflict") with a user and a group behind it; the
 * wall proof lists one allowed (breach) and two denied attempts, the access review three people and a group. Requests
 * received are recorded in `requests` (path and query), as the server audits every generation.
 */
export class ComplianceMock {
  readonly requests: { path: string; query: string }[] = [];

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const match = /^\/api\/v1\/workspaces\/[^/]+\/compliance\/(wall-proof|access-review)$/.exec(
      path,
    );
    if (!match || method !== 'GET') return undefined;
    this.requests.push({ path, query: url.search });
    const body = match[1] === 'wall-proof' ? this.wallProof(url) : this.accessReview();
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
  }

  private wallProof(url: URL) {
    const attempt = (n: number, result: string, user: string, outcome: string) => ({
      eventId: `0199c3a0-0000-7000-8000-00000000e00${n}`,
      occurredAt: `2026-10-0${n + 1}T10:15:00Z`,
      result,
      userId: user === 'Morgan Walled' ? 'user-9' : 'user-8',
      userDisplay: user,
      documentId: DOC,
      wallIds: [WALL],
      action: 'Document.Retrieved',
      outcome,
      reasonCode: outcome === 'Denied' ? 'EthicalWall' : null,
      accessPath: 'Normal',
      correlationId: `corr-${n}`,
    });
    return {
      from: url.searchParams.get('from') ?? '2026-09-11T00:00:00Z',
      to: url.searchParams.get('to') ?? '2026-10-11T00:00:00Z',
      generatedAt: '2026-10-11T09:00:00Z',
      held: false,
      counts: { attempts: 3, denied: 2, breakGlass: 0, allowed: 1, failed: 0 },
      walls: [
        {
          wallId: WALL,
          name: 'Northwind conflict',
          createdAt: '2026-09-01T08:00:00Z',
          members: [
            {
              userId: 'user-9',
              group: null,
              displayName: 'Morgan Walled',
              addedAt: '2026-09-01T08:00:00Z',
            },
            {
              userId: null,
              group: 'Northwind team',
              displayName: null,
              addedAt: '2026-09-01T08:00:00Z',
            },
          ],
          documentsCovered: 42,
        },
      ],
      attempts: [
        attempt(3, 'Allowed', 'Morgan Walled', 'Success'),
        attempt(1, 'Denied', 'Morgan Walled', 'Denied'),
        attempt(2, 'Denied', 'Casey Grouped', 'Denied'),
      ],
      truncated: false,
      wallChanges: [
        {
          eventId: '0199c3a0-0000-7000-8000-00000000c001',
          occurredAt: '2026-09-20T12:00:00Z',
          action: 'WallChanged',
          actorDisplay: 'Dana Counsel',
          wallId: WALL,
        },
      ],
    };
  }

  private accessReview() {
    const role = (r: string, displayName: string, viaGroup: string | null = null) => ({
      role: r,
      displayName,
      viaGroup,
      assignedAt: '2026-09-01T08:00:00Z',
    });
    const wall = (viaGroup: string | null = null) => ({
      wallId: WALL,
      name: 'Northwind conflict',
      viaGroup,
    });
    return {
      generatedAt: '2026-10-11T09:00:00Z',
      users: 3,
      groups: 1,
      entries: [
        {
          principal: 'User',
          userId: 'user-1',
          group: null,
          displayName: 'Alex Reviewer',
          email: 'alex@example.test',
          roles: [role('WorkspaceAdmin', 'Workspace Admin')],
          walls: [],
          lastAccessAt: '2026-10-10T16:30:00Z',
          lastSignInAt: '2026-10-10T08:00:00Z',
          knownMembers: null,
        },
        {
          principal: 'User',
          userId: 'user-8',
          group: null,
          displayName: 'Casey Grouped',
          email: null,
          roles: [role('Reviewer', 'Reviewer', 'Northwind team')],
          walls: [wall('Northwind team')],
          lastAccessAt: null,
          lastSignInAt: '2026-10-02T08:00:00Z',
          knownMembers: null,
        },
        {
          principal: 'User',
          userId: 'user-9',
          group: null,
          displayName: 'Morgan Walled',
          email: 'morgan@example.test',
          roles: [role('Reviewer', 'Reviewer')],
          walls: [wall()],
          lastAccessAt: '2026-10-04T10:15:00Z',
          lastSignInAt: '2026-10-04T08:00:00Z',
          knownMembers: null,
        },
        {
          principal: 'Group',
          userId: null,
          group: 'Northwind team',
          displayName: null,
          email: null,
          roles: [role('Reviewer', 'Reviewer')],
          walls: [wall()],
          lastAccessAt: null,
          lastSignInAt: null,
          knownMembers: 1,
        },
      ],
    };
  }
}
