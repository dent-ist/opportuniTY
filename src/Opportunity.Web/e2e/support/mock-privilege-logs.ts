import type { Route } from '@playwright/test';

/**
 * Privilege logs (E13-T03) for the e2e mock API, in the shapes of `GET …/productions` (finalized and draft),
 * `GET …/privilege-log-templates`, `GET`/`POST …/privilege-logs` and `GET …/privilege-logs/{id}/content` (CSV/XLSX
 * through the gateway). A generation adds the next version unless the template and production match the latest one.
 */

export interface PrivilegeLogRequest {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
}

const PAT = { userId: 'user-1', displayName: 'Alex Reviewer' };

const PRODUCTIONS = [
  {
    productionId: 'prod-1',
    name: 'Volume 1',
    version: 1,
    status: 'finalized',
    bates: { first: 'ACM0000001', last: 'ACM0000120' },
  },
  {
    productionId: 'prod-2',
    name: 'Volume 2',
    version: 1,
    status: 'draft',
    bates: { first: null, last: null },
  },
];

const COLUMNS = [
  'Priv ID',
  'Date',
  'From',
  'To',
  'Subject/File Name',
  'Privilege Basis',
  'Description',
  'Family Range',
  'Withheld/Redacted',
];

function log(version: number, template: string, sha: string) {
  return {
    logId: `plog-${version}`,
    version,
    source: 'production',
    productionId: 'prod-1',
    snapshotId: 'snap-1',
    reviewSetSnapshotId: null,
    templateId: template === 'Supply case log' ? 'tpl-1' : null,
    templateName: template,
    contentSha256: sha.repeat(64),
    files: [
      { format: 'csv', sha256: 'b'.repeat(64), bytes: 2048 },
      { format: 'xlsx', sha256: 'c'.repeat(64), bytes: 6144 },
    ],
    metadata: {
      formatVersion: 1,
      source: 'production',
      productionId: 'prod-1',
      productionName: 'Volume 1',
      productionVersion: 1,
      snapshotId: 'snap-1',
      reviewSetSnapshotId: null,
      templateId: null,
      templateName: template,
      columns: COLUMNS,
      privacyRedactionsIncluded: false,
      exclusionRules:
        template === 'Supply case log'
          ? [
              {
                label: 'Communications with outside counsel after the complaint',
                dateField: 'Document Date',
                onOrAfter: '2026-01-15',
                before: null,
                logCategories: [],
                attorneysInvolved: [],
                excludedDocuments: 4,
              },
            ]
          : [],
      entries: 14,
      withheld: 11,
      redacted: 3,
      redactedPrivacy: 0,
      excludedByRules: template === 'Supply case log' ? 4 : 0,
    },
    generatedBy: PAT,
    generatedAt: `2026-10-0${version + 1}T09:30:00Z`,
  };
}

export class PrivilegeLogsMock {
  readonly requests: PrivilegeLogRequest[] = [];
  private readonly logs = [log(1, 'Document-by-document', 'a')];

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const request = route.request();
    let body: Record<string, unknown> | null = null;
    try {
      body = request.postDataJSON() as Record<string, unknown> | null;
    } catch {
      body = null;
    }
    if (/^\/api\/v1\/workspaces\/[^/]+\/productions$/.test(path) && method === 'GET') {
      return route.fulfill({
        json: { items: PRODUCTIONS, nextCursor: null, total: { value: 2, relation: 'eq' } },
      });
    }
    if (/^\/api\/v1\/workspaces\/[^/]+\/privilege-log-templates$/.test(path) && method === 'GET') {
      const column = (header: string) => ({ kind: 'field', header });
      return route.fulfill({
        json: {
          items: [
            {
              templateId: 'tpl-1',
              name: 'Supply case log',
              definition: { columns: COLUMNS.map(column) },
              modifiedBy: PAT,
              modifiedAt: '2026-10-08T08:00:00Z',
              version: 1,
            },
          ],
          presets: [
            {
              preset: 'documentByDocument',
              name: 'Document-by-document',
              definition: { columns: COLUMNS.map(column) },
            },
            {
              preset: 'metadataOnly',
              name: 'Metadata only',
              definition: { columns: COLUMNS.slice(0, 6).map(column) },
            },
          ],
        },
      });
    }
    const match = path.match(
      /^\/api\/v1\/workspaces\/[^/]+\/privilege-logs(?:\/([^/]+)(\/content)?)?$/,
    );
    if (!match) return undefined;
    this.requests.push({ method, path, body });
    if (method === 'GET' && !match[1]) {
      return route.fulfill({
        json: {
          items: [...this.logs].reverse(),
          nextCursor: null,
          total: { value: this.logs.length, relation: 'eq' },
        },
      });
    }
    if (method === 'POST' && !match[1]) {
      const template =
        body?.['templateId'] === 'tpl-1' ? 'Supply case log' : 'Document-by-document';
      const latest = this.logs[this.logs.length - 1];
      if (latest.templateName === template) {
        return route.fulfill({ status: 200, json: { log: latest, unchanged: true } });
      }
      const next = log(this.logs.length + 1, template, 'd');
      this.logs.push(next);
      return route.fulfill({ status: 201, json: { log: next, unchanged: false } });
    }
    if (method === 'GET' && match[2]) {
      return route.fulfill({
        status: 200,
        contentType: 'text/csv; charset=utf-8',
        headers: { 'Content-Disposition': 'attachment; filename="Volume 1 privilege log v1.csv"' },
        body: `${COLUMNS.join(',')}\r\n`,
      });
    }
    return route.fulfill({ status: 405, json: { title: 'Method not allowed' } });
  }
}
