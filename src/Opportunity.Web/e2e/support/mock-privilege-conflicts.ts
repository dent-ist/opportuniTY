import type { Route } from '@playwright/test';

/**
 * Privilege conflicts (E13-T02) for the e2e mock API, in the shapes of `GET …/privilege-conflicts`,
 * `GET …/privilege-conflicts/export` (CSV through the gateway) and `POST …/privilege-conflicts/propagations`
 * (202 with a bulk coding job, Idempotency-Key). After a propagation the duplicate group it named is consistent.
 */

export interface PrivilegeConflictRequest {
  method: string;
  path: string;
  query: string;
  body: Record<string, unknown> | null;
  idempotencyKey: string | null;
}

const ALEX = { userId: 'user-1', displayName: 'Alex Reviewer' };
const JAMIE = { userId: 'user-2', displayName: 'Jamie Lee' };

function value(values: string[], by = ALEX) {
  return {
    values,
    choiceIds: values.map((_, i) => i + 1),
    changedBy: values.length ? by : null,
    changedAt: values.length ? '2026-10-07T14:05:00Z' : null,
  };
}

function member(
  documentId: string,
  controlNumber: string,
  status: string[],
  extra: Record<string, unknown> = {},
) {
  return {
    documentId,
    controlNumber,
    familySequence: 0,
    isPrimary: false,
    inProduction: false,
    privilegeStatus: value(status),
    privilegeBasis: value(status.includes('Withhold') ? ['Attorney-Client'] : []),
    responsiveness: null as ReturnType<typeof value> | null,
    ...extra,
  };
}

export class PrivilegeConflictsMock {
  readonly requests: PrivilegeConflictRequest[] = [];
  private readonly resolved = new Set<string>();

  private groups(responsiveness: boolean) {
    const resp = (v: string) => (responsiveness ? value([v], JAMIE) : null);
    const groups = [
      {
        kind: 'family',
        groupId: 'fam-1',
        reasons: ['withheldMember', ...(responsiveness ? ['responsivenessDiffers'] : [])],
        members: [
          member('doc-1', 'ACM0000001', ['Withhold'], { responsiveness: resp('Responsive') }),
          member('doc-2', 'ACM0000002', ['Not Privileged'], {
            familySequence: 1,
            privilegeStatus: value(['Not Privileged'], JAMIE),
            responsiveness: resp('Not Responsive'),
          }),
        ],
      },
      {
        kind: 'duplicates',
        groupId: 'dup-1',
        reasons: ['privilegeCallsDiffer'],
        members: [
          member('doc-5', 'ACM0000005', ['Not Privileged'], {
            isPrimary: true,
            privilegeStatus: value(['Not Privileged'], JAMIE),
          }),
          member('doc-6', 'ACM0000006', ['Withhold']),
          member('doc-7', 'ACM0000007', []),
        ],
      },
      {
        kind: 'duplicates',
        groupId: 'dup-2',
        reasons: ['privilegeCallsDiffer'],
        members: [
          member('doc-8', 'ACM0000008', ['Redact'], { isPrimary: true }),
          member('doc-9', 'ACM0000009', ['Not Privileged'], {
            privilegeStatus: value(['Not Privileged'], JAMIE),
          }),
        ],
      },
    ];
    return groups.filter((g) => !this.resolved.has(g.groupId));
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const match = path.match(
      /^\/api\/v1\/workspaces\/[^/]+\/privilege-conflicts(\/export|\/propagations)?$/,
    );
    if (!match) return undefined;
    const request = route.request();
    let body: Record<string, unknown> | null = null;
    try {
      body = request.postDataJSON() as Record<string, unknown> | null;
    } catch {
      body = null;
    }
    this.requests.push({
      method,
      path,
      query: url.search,
      body,
      idempotencyKey: request.headers()['idempotency-key'] ?? null,
    });
    const responsiveness = url.searchParams.has('responsivenessField');
    if (method === 'GET' && !match[1]) {
      const groups = this.groups(responsiveness);
      return route.fulfill({
        json: {
          generatedAt: '2026-10-08T09:30:00Z',
          responsivenessFieldId: responsiveness
            ? Number(url.searchParams.get('responsivenessField'))
            : null,
          productionId: null,
          familyConflictCount: groups.filter((g) => g.kind === 'family').length,
          duplicateConflictCount: groups.filter((g) => g.kind === 'duplicates').length,
          truncated: false,
          groups,
        },
      });
    }
    if (method === 'GET' && match[1] === '/export') {
      return route.fulfill({
        status: 200,
        contentType: 'text/csv; charset=utf-8',
        headers: { 'Content-Disposition': 'attachment; filename="privilege-conflicts.csv"' },
        body: 'Conflict,Group ID,Reasons,Document ID,Control Number\r\n',
      });
    }
    if (method === 'POST' && match[1] === '/propagations') {
      const groups = (body?.['groups'] as { duplicateGroupId: string }[] | undefined) ?? [];
      for (const g of groups) this.resolved.add(g.duplicateGroupId);
      return route.fulfill({
        status: 202,
        headers: { Location: '/api/v1/workspaces/ws-1/jobs/job-priv-1' },
        json: { jobId: 'job-priv-1', jobType: 'bulkCoding', status: 'running' },
      });
    }
    return route.fulfill({ status: 405, json: { title: 'Method not allowed' } });
  }
}
