import type { Route } from '@playwright/test';

/**
 * Privilege logs (E13-T03, #182) for the e2e mock API, in the shapes of `GET …/productions` (finalized and draft),
 * `GET …/privilege-log-templates`, `GET`/`POST …/privilege-logs`, `GET …/privilege-logs/{id}/entries` and
 * `…/changes`, `GET …/privilege-logs/{id}/content` (CSV/XLSX through the gateway), and the Privilege Description of
 * the logged documents (`GET`/`PUT …/documents/pl-doc-N/coding`, `GET …/coding-history`). A generation adds the
 * next version unless the template and production match the latest one and no description was edited since.
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

const DESCRIPTION_FIELD = 39;

function entries(descriptions: Record<string, string>) {
  return [1, 2, 3].map((n) => ({
    ordinal: n,
    documentId: `pl-doc-${n}`,
    treatment: n === 3 ? 'redacted' : 'withheld',
    cells: [
      n === 3 ? 'ACM0000044' : `PRIV000${n}`,
      '2025-06-01',
      'Riley Counsel',
      'Morgan Client',
      `Supply agreement ${n}`,
      n === 2 ? 'Work Product' : 'Attorney-Client',
      descriptions[`pl-doc-${n}`] ?? '',
      '',
      n === 3 ? 'Redacted' : 'Withheld',
    ],
  }));
}

function log(version: number, template: string, sha: string) {
  return {
    logId: `plog-${version}`,
    version,
    descriptionColumn: 6,
    savedSearchId: null,
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
  /** Privilege Description by document now, and as each version froze it. */
  private readonly descriptions: Record<string, string> = { 'pl-doc-1': 'Advice of counsel' };
  private readonly frozen = new Map<string, Record<string, string>>([
    ['plog-1', { 'pl-doc-1': 'Advice of counsel' }],
  ]);
  private documentVersion = 4;
  private readonly history: {
    kind: string;
    occurredAt: string;
    actor: { userId: string; displayName: string };
    oldValue: string | null;
    newValue: string | null;
  }[] = [];

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
    const coding =
      /^\/api\/v1\/workspaces\/[^/]+\/documents\/(pl-doc-\d+)\/(coding|coding-history)$/.exec(path);
    if (coding) {
      this.requests.push({ method, path, body });
      return this.coding(route, method, coding[1], coding[2]);
    }
    const match = path.match(
      /^\/api\/v1\/workspaces\/[^/]+\/privilege-logs(?:\/([^/]+)(\/content|\/entries|\/changes)?)?$/,
    );
    if (!match) return undefined;
    this.requests.push({ method, path, body });
    if (method === 'GET' && match[2] === '/entries') {
      const items = entries(this.frozen.get(match[1]) ?? {});
      return route.fulfill({
        json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
      });
    }
    if (method === 'GET' && match[2] === '/changes') {
      return route.fulfill({ json: this.changes(match[1]) });
    }
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
      const same =
        JSON.stringify(this.frozen.get(latest.logId)) === JSON.stringify(this.descriptions);
      if (latest.templateName === template && same) {
        return route.fulfill({ status: 200, json: { log: latest, unchanged: true } });
      }
      const next = log(this.logs.length + 1, template, 'd');
      this.logs.push(next);
      this.frozen.set(next.logId, { ...this.descriptions });
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

  /** Compares a version with the previous one, the way `GET …/changes` does (by document, cell by column). */
  private changes(logId: string) {
    const index = this.logs.findIndex((l) => l.logId === logId);
    const current = this.logs[index];
    const previous = index > 0 ? this.logs[index - 1] : null;
    const base = {
      logId,
      version: current?.version ?? 1,
      previousLogId: previous?.logId ?? null,
      previousVersion: previous?.version ?? null,
      added: 0,
      removed: 0,
      changed: 0,
      unchanged: 0,
      columnsAdded: [],
      columnsRemoved: [],
      items: [] as unknown[],
      nextCursor: null,
    };
    if (!previous) return base;
    const now = entries(this.frozen.get(logId) ?? {});
    const before = entries(this.frozen.get(previous.logId) ?? {});
    for (const row of now) {
      const old = before.find((b) => b.documentId === row.documentId)!;
      const changedCells = COLUMNS.flatMap((column, i) =>
        old.cells[i] === row.cells[i]
          ? []
          : [{ column, before: old.cells[i], after: row.cells[i] }],
      );
      if (changedCells.length === 0) {
        base.unchanged++;
        continue;
      }
      base.changed++;
      base.items.push({
        kind: 'changed',
        documentId: row.documentId,
        ordinal: row.ordinal,
        previousOrdinal: old.ordinal,
        treatment: row.treatment,
        cells: row.cells,
        changedCells,
      });
    }
    return base;
  }

  private coding(route: Route, method: string, documentId: string, kind: string): Promise<void> {
    const view = () => ({
      documentId,
      documentVersion: this.documentVersion,
      projectedVersion: this.documentVersion,
      layoutId: null,
      lastEditor: null,
      indexingState: 'searchable',
      fields: [
        {
          fieldId: DESCRIPTION_FIELD,
          value: this.descriptions[documentId] ?? null,
          editable: true,
          isSecurityAffecting: false,
          changedAt: null,
          changedAtVersion: null,
          changedBy: null,
        },
      ],
    });
    if (kind === 'coding-history' && method === 'GET') {
      return route.fulfill({ json: { items: [...this.history].reverse(), nextCursor: null } });
    }
    if (kind === 'coding' && method === 'GET') return route.fulfill({ json: view() });
    if (kind === 'coding' && method === 'PUT') {
      const request = route.request();
      if (request.headers()['if-match'] !== `"${this.documentVersion}"`) {
        return route.fulfill({ status: 412, json: { title: 'Version conflict', status: 412 } });
      }
      const change = (request.postDataJSON() as { changes: { value: string | null }[] }).changes[0];
      this.history.push({
        kind: 'changed',
        occurredAt: '2026-10-11T09:00:00Z',
        actor: PAT,
        oldValue: this.descriptions[documentId] ?? null,
        newValue: change.value,
      });
      this.descriptions[documentId] = change.value ?? '';
      this.documentVersion++;
      return route.fulfill({ json: view() });
    }
    return route.fulfill({ status: 405, json: { title: 'Method not allowed' } });
  }
}
