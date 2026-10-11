import type { Route } from '@playwright/test';

/**
 * Productions and exports (E12-T08) for the e2e mock API, in the shapes of `…/productions` (list, one with its ETag,
 * create, update, Bates allocation, QC run/exceptions, finalize with overrides, discard, void, documents, volumes,
 * verification) and `…/exports` (list, one, create, exclusions, files), plus the frozen sets they name. prod-1 is
 * finalized (ACM0000001 – ACM0000120, two identical volume runs); prod-2 is a draft with Bates numbers assigned whose
 * QC finds a render failure (overridable) and a warning. Gateway downloads are links only and never fetched here.
 */

export interface DeliveryRequest {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  ifMatch: string | null;
  idempotencyKey: string | null;
}

const ME = 'user-1';
const SHA = 'c0ffee'.repeat(10) + 'c0ff';
const SHA2 = 'beef'.repeat(16);

type Production = Record<string, unknown> & {
  productionId: string;
  status: string;
  version: number;
  rowVersion: number;
  bates: Record<string, unknown>;
  specification: Record<string, unknown>;
};

function specification(prefix: string, start: number) {
  return {
    bates: { prefix, startNumber: start, padding: 7, suffix: null, level: 'page' },
    withheldDocuments: 'placeholder',
    images: { format: 'tiffG4', dpi: 300, colorFileTypes: ['jpg', 'png'] },
    includeText: true,
    defaultOutput: 'image',
    fileTypeRules: [{ extensions: ['xlsx', 'csv'], output: 'native' }],
    redactionSetId: null,
    endorsements: {
      items: [
        { position: 'bottomLeft', template: '{confidentiality}' },
        { position: 'bottomRight', template: '{bates}' },
      ],
      fontSize: 10,
      expandCanvas: false,
      margin: 18,
    },
    placeholders: {
      withheld: 'Withheld – Privileged',
      technicalIssue: 'Technical Issue',
      nativeSlipSheet: 'Document Produced in Native Format',
    },
    loadFile: {
      delimiters: 'concordance',
      encoding: 'utf-8',
      volume: { prefix: `${prefix}_VOL` },
      fields: [{ column: 'prodBegBates', header: 'ProdBegBates' }],
    },
  };
}

function bates(first: number | null, last: number | null, documents: number | null) {
  const label = (n: number) => `ACM${String(n).padStart(7, '0')}`;
  return first === null || last === null
    ? {
        state: 'none',
        first: null,
        last: null,
        firstNumber: null,
        lastNumber: null,
        documents: null,
        numbers: null,
        integrity: null,
        reason: null,
        assignmentsSha256: null,
      }
    : {
        state: 'allocated',
        first: label(first),
        last: label(last),
        firstNumber: first,
        lastNumber: last,
        documents,
        numbers: last - first + 1,
        integrity: {
          passed: true,
          documents,
          numbers: last - first + 1,
          gaps: 0,
          placeholders: 1,
          nativeSlipSheets: 1,
          problems: [],
        },
        reason: null,
        assignmentsSha256: SHA2,
      };
}

function production(
  id: string,
  name: string,
  status: string,
  start: number,
  range: [number, number, number] | null,
): Production {
  return {
    productionId: id,
    workspaceId: 'ws-1',
    lineageId: `lineage-${id}`,
    name,
    version: 1,
    rowVersion: 1,
    status,
    snapshotId: `snap-${id}`,
    specification: specification('ACM', start),
    specificationSha256: SHA2,
    bates: range ? bates(...range) : bates(null, null, null),
    batesJob: null,
    manifest: status === 'finalized' ? JSON.stringify({ production: id, version: 1 }) : null,
    manifestSha256: status === 'finalized' ? SHA : null,
    createdBy: ME,
    createdAt: '2026-10-01T09:00:00Z',
    modifiedAt: '2026-10-01T09:00:00Z',
    finalizedAt: status === 'finalized' ? '2026-10-02T12:00:00Z' : null,
    voidedAt: null,
    voidReason: null,
    discardedAt: null,
  };
}

function check(
  name: string,
  title: string,
  status: string,
  documents = 0,
  overridable = false,
  severity = 'blocking',
) {
  return {
    check: name,
    title,
    severity,
    status,
    documents,
    overridable,
    overrideReason: null as string | null,
    remedy: 'Fix the listed documents, then run the checks again.',
  };
}

function qc(productionId: string, outcome: string, checks: unknown[], purpose = 'check') {
  return {
    productionId,
    qcRunId: `qc-${productionId}-${purpose}`,
    purpose,
    outcome,
    runAt: '2026-10-03T08:00:00Z',
    runBy: ME,
    reportSha256: SHA2,
    warningsAcknowledged: purpose === 'finalization',
    checks,
  };
}

function job(id: string, type: string) {
  return {
    jobId: id,
    jobType: type,
    workspaceId: 'ws-1',
    status: 'completed',
    statusReason: null,
    initiatedBy: ME,
    targetSnapshotId: null,
    createdAt: '2026-10-03T10:00:00Z',
    startedAt: '2026-10-03T10:00:00Z',
    finishedAt: '2026-10-03T10:01:00Z',
    committed: {
      chunksTotal: 1,
      chunksCommitted: 1,
      chunksFailed: 0,
      chunksPending: 0,
      chunksCancelled: 0,
      itemsApplied: 1,
      itemsUnchanged: 0,
      itemsFailed: 0,
      itemsExcludedNoAccess: 0,
      itemsSkippedConcurrentEdit: 0,
    },
    indexed: { state: 'current', indexTasksApplied: 0, indexTasksTotal: 0 },
  };
}

function volume(id: string, productionId: string, createdAt: string) {
  return {
    volumeId: id,
    productionId,
    workspaceId: 'ws-1',
    volume: 'ACM_VOL001',
    status: 'completed',
    statusReason: null,
    report: {
      documents: 30,
      images: 112,
      natives: 1,
      texts: 30,
      files: 146,
      totalBytes: 7_340_032,
      manifestSha256: SHA,
    },
    verification: {
      status: 'passed',
      documents: 30,
      pages: 112,
      boxes: 6,
      failures: 0,
      reportSha256: SHA2,
    },
    job: job(`job-${id}`, 'productionVolume'),
    createdBy: ME,
    createdAt,
    completedAt: createdAt,
  };
}

function exportResource(
  id: string,
  name: string,
  snapshotId: string,
  body?: Record<string, unknown>,
) {
  const volumeReq = (body?.['volume'] ?? {}) as Record<string, unknown>;
  const prefix = String(volumeReq['prefix'] ?? 'VOL');
  const start = Number(volumeReq['start'] ?? 1);
  const padding = Number(volumeReq['padding'] ?? 3);
  const fields = (body?.['fields'] as Record<string, unknown>[] | undefined) ?? [
    { fieldId: 1, header: 'BEGDOC' },
    { column: 'familyId', header: 'FamilyID' },
  ];
  return {
    exportId: id,
    workspaceId: 'ws-1',
    name,
    snapshotId,
    status: 'completed',
    statusReason: null,
    settings: {
      volumeName: `${prefix}${String(start).padStart(padding, '0')}`,
      maxFilesPerFolder: Number(volumeReq['maxFilesPerFolder'] ?? 1000),
      includeNatives: body?.['includeNatives'] ?? true,
      includeText: body?.['includeText'] ?? true,
      includeImages: body?.['includeImages'] ?? true,
      textEncoding: body?.['textEncoding'] ?? 'utf-8',
      pathSeparator: body?.['pathSeparator'] ?? '\\',
      delimiters: body?.['delimiters'] ?? 'concordance',
      encoding: body?.['encoding'] ?? 'utf-8',
      fields: fields.map((f) => ({
        fieldId: f['fieldId'] ?? null,
        column: f['column'] ?? null,
        header: f['header'] ?? '',
      })),
    },
    report: {
      documentsExported: 118,
      documentsExcluded: 2,
      natives: 118,
      texts: 118,
      images: 240,
      pages: 240,
      files: 480,
      totalBytes: 12_582_912,
      manifestSha256: SHA,
    },
    job: job(`job-${id}`, 'export'),
    createdBy: ME,
    createdAt: '2026-10-04T09:00:00Z',
    completedAt: '2026-10-04T09:03:00Z',
  };
}

function frozenSet(id: string, count: number, purpose: string) {
  return {
    snapshotId: id,
    name: 'Frozen set',
    purpose,
    status: 'ready',
    statusReason: null,
    source: {
      kind: 'query',
      query: null,
      normalizedQuery: null,
      snapshotId: null,
      requestedCount: null,
    },
    documentCount: count,
    inclusionCounts: { Family: 4 },
    searchGeneration: 18432,
    projectionGeneration: 18432,
    selectedWhileIndexing: false,
    selectedAt: '2026-10-01T08:59:00Z',
    materializationStrategy: 'Inline',
    pageSize: 1000,
    pageCount: 1,
    rootSha256: SHA2,
    createdBy: ME,
    createdAt: '2026-10-01T08:59:00Z',
    materializedAt: '2026-10-01T08:59:01Z',
    expiresAt: null,
    expiredAt: null,
  };
}

const SNAPSHOTS: Record<string, [number, string]> = {
  'snap-prod-1': [30, 'production'],
  'snap-prod-2': [12, 'production'],
  'snap-exp-1': [120, 'export'],
};

export class ProductionsMock {
  readonly requests: DeliveryRequest[] = [];
  readonly productions = new Map<string, Production>([
    ['prod-2', production('prod-2', 'Volume 2', 'draft', 121, [121, 180, 12])],
    ['prod-1', production('prod-1', 'Volume 1', 'finalized', 1, [1, 120, 30])],
  ]);
  private readonly qc = new Map<string, unknown>([
    [
      'prod-1',
      qc(
        'prod-1',
        'passed',
        [
          check('batesOverlap', 'Bates numbers used by another production', 'passed'),
          check('textMissing', 'Documents without text', 'acknowledged', 1, false, 'warning'),
        ],
        'finalization',
      ),
    ],
  ]);
  private readonly volumes = new Map<string, unknown[]>([
    [
      'prod-1',
      [
        volume('vol-2', 'prod-1', '2026-10-04T10:00:00Z'),
        volume('vol-1', 'prod-1', '2026-10-03T10:00:00Z'),
      ],
    ],
  ]);
  readonly exports = new Map<string, ReturnType<typeof exportResource>>([
    ['exp-1', exportResource('exp-1', 'Responsive emails export', 'snap-exp-1')],
  ]);
  private next = 3;

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const ws = /^\/api\/v1\/workspaces\/[^/]+\/(.*)$/.exec(path)?.[1];
    if (!ws) return undefined;
    const snapshotRead = /^snapshots\/(snap-(?:prod|exp)-\d+)$/.exec(ws);
    if (snapshotRead && method === 'GET' && SNAPSHOTS[snapshotRead[1]]) {
      const [count, purpose] = SNAPSHOTS[snapshotRead[1]];
      return route.fulfill({ json: frozenSet(snapshotRead[1], count, purpose) });
    }
    if (!ws.startsWith('productions') && !ws.startsWith('exports')) return undefined;
    const request = route.request();
    let body: Record<string, unknown> | null = null;
    try {
      body = request.postDataJSON() as Record<string, unknown> | null;
    } catch {
      body = null;
    }
    const headers = request.headers();
    this.requests.push({
      method,
      path,
      body,
      ifMatch: headers['if-match'] ?? null,
      idempotencyKey: headers['idempotency-key'] ?? null,
    });
    return ws.startsWith('exports')
      ? this.exportRoute(route, method, ws, body)
      : this.productionRoute(route, method, ws, body, headers['if-match'] ?? null);
  }

  private resource(p: Production) {
    const { rowVersion, ...rest } = p;
    void rowVersion;
    return rest;
  }

  private send(route: Route, p: Production, status = 200) {
    return route.fulfill({
      status,
      json: this.resource(p),
      headers: { ETag: `"${p.rowVersion}"` },
    });
  }

  private productionRoute(
    route: Route,
    method: string,
    ws: string,
    body: Record<string, unknown> | null,
    ifMatch: string | null,
  ): Promise<void> | undefined {
    if (ws === 'productions' && method === 'GET') {
      const items = [...this.productions.values()].map((p) => this.resource(p));
      return route.fulfill({
        json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
      });
    }
    if (ws === 'productions' && method === 'POST') {
      const id = `prod-${this.next++}`;
      const previous = body?.['previousVersionId']
        ? this.productions.get(String(body['previousVersionId']))
        : undefined;
      const p = production(id, String(body?.['name'] ?? 'Production 2026-10-11'), 'draft', 1, null);
      p['snapshotId'] = previous ? previous['snapshotId'] : String(body?.['snapshotId']);
      p.specification = {
        ...specification('ACM', 1),
        ...((body?.['specification'] as Record<string, unknown>) ?? previous?.specification ?? {}),
      };
      if (previous) {
        p.version = previous.version + 1;
        p['name'] = String(previous['name']);
      }
      this.productions.set(id, p);
      return this.send(route, p, 201);
    }
    const m = /^productions\/([^/]+)(?:\/(.*))?$/.exec(ws);
    const p = m ? this.productions.get(m[1]) : undefined;
    if (!m || !p) return route.fulfill({ status: 404, json: { title: 'Not found', status: 404 } });
    const action = m[2] ?? '';
    const stale = () =>
      ifMatch !== `"${p.rowVersion}"`
        ? route.fulfill({ status: 412, json: { title: 'Modified', status: 412 } })
        : null;
    if (action === '' && method === 'GET') return this.send(route, p);
    if (action === '' && method === 'PUT') {
      const refused = stale();
      if (refused) return refused;
      p.specification = body?.['specification'] as Record<string, unknown>;
      p['name'] = body?.['name'] ?? p['name'];
      p.bates = bates(null, null, null);
      p.rowVersion++;
      this.qc.delete(p.productionId);
      return this.send(route, p);
    }
    if (action === 'bates-allocation' && method === 'POST') {
      const start = Number((p.specification['bates'] as Record<string, unknown>)['startNumber']);
      p.bates = bates(start, start + 59, 12);
      p.rowVersion++;
      return route.fulfill({
        status: 202,
        json: job(`job-bates-${p.productionId}`, 'batesAllocation'),
      });
    }
    if (action === 'qc' && method === 'POST') {
      const report = qc(p.productionId, 'blocked', [
        check('renderFailure', 'Pages that could not be rendered', 'failed', 2, true),
        check('batesOverlap', 'Bates numbers used by another production', 'passed'),
        check('textMissing', 'Documents without text', 'warning', 1, false, 'warning'),
      ]);
      this.qc.set(p.productionId, report);
      return route.fulfill({ json: report });
    }
    if (action === 'qc' && method === 'GET') {
      const report = this.qc.get(p.productionId);
      return report
        ? route.fulfill({ json: report })
        : route.fulfill({ status: 404, json: { title: 'No QC run', status: 404 } });
    }
    if (action === 'qc/exceptions' && method === 'GET') {
      return route.fulfill({
        json: {
          qcRunId: `qc-${p.productionId}-check`,
          restricted: 1,
          nextCursor: null,
          items: [
            {
              check: 'renderFailure',
              documentId: 'doc-4',
              controlNumber: 'DEMO-000004',
              prodBegBates: 'ACM0000130',
              prodEndBates: 'ACM0000133',
              sequence: 4,
              detail: 'Page 2 could not be rendered',
            },
          ],
        },
      });
    }
    if (action === 'finalize' && method === 'POST') {
      const refused = stale();
      if (refused) return refused;
      const overrides = (body?.['qcOverrides'] as { check: string; reason: string }[]) ?? [];
      const overridden = overrides.find((o) => o.check === 'renderFailure' && o.reason);
      if (!overridden || body?.['acknowledgeWarnings'] !== true) {
        return route.fulfill({
          status: 409,
          json: {
            title: 'The QC checks failed',
            detail: 'Override or fix the failed checks and acknowledge the warnings.',
            status: 409,
            reason: 'QC_FAILED',
            qc: this.qc.get(p.productionId),
          },
        });
      }
      p.status = 'finalized';
      p['finalizedAt'] = '2026-10-11T09:00:00Z';
      p['manifest'] = JSON.stringify({ production: p.productionId });
      p['manifestSha256'] = SHA;
      p.rowVersion++;
      const passed = qc(
        p.productionId,
        'passed',
        [
          {
            ...check('renderFailure', 'Pages that could not be rendered', 'overridden', 2, true),
            overrideReason: overridden.reason,
          },
          check('textMissing', 'Documents without text', 'acknowledged', 1, false, 'warning'),
        ],
        'finalization',
      );
      this.qc.set(p.productionId, passed);
      return this.send(route, p);
    }
    if ((action === 'discard' || action === 'void') && method === 'POST') {
      const refused = stale();
      if (refused) return refused;
      p.status = action === 'discard' ? 'discarded' : 'voided';
      if (action === 'void') p['voidReason'] = body?.['reason'] ?? null;
      p.rowVersion++;
      return this.send(route, p);
    }
    if (action === 'documents' && method === 'GET') {
      const first = Number(p.bates['firstNumber'] ?? 1);
      const items = Array.from({ length: 5 }, (_, i) => {
        const beg = first + i * 4;
        const label = (n: number) => `ACM${String(n).padStart(7, '0')}`;
        return {
          documentId: `doc-${i + 1}`,
          sequence: i + 1,
          controlNumber: `DEMO-${String(i + 1).padStart(6, '0')}`,
          prodBegBates: label(beg),
          prodEndBates: label(beg + 3),
          prodBegAttach: label(beg),
          prodEndAttach: label(beg + 3),
          numbers: 4,
          output: i === 2 ? 'native' : 'image',
          designation: 'Confidential',
        };
      });
      return route.fulfill({ json: { items, nextCursor: null } });
    }
    if (action === 'volumes' && method === 'GET') {
      const items = this.volumes.get(p.productionId) ?? [];
      return route.fulfill({ json: { items, nextCursor: null } });
    }
    if (action === 'volumes' && method === 'POST') {
      const list = this.volumes.get(p.productionId) ?? [];
      const v = volume(`vol-${list.length + 1}`, p.productionId, '2026-10-11T10:00:00Z');
      this.volumes.set(p.productionId, [v, ...list]);
      return route.fulfill({ status: 202, json: v });
    }
    const files = /^volumes\/([^/]+)\/files$/.exec(action);
    if (files && method === 'GET') {
      return route.fulfill({
        json: {
          items: [
            {
              fileId: 'vf-1',
              path: 'MANIFEST.json',
              kind: 'manifest',
              sizeBytes: 4096,
              sha256: SHA,
            },
            {
              fileId: 'vf-2',
              path: 'ACM_VOL001/DATA/ACM_VOL001.dat',
              kind: 'dat',
              sizeBytes: 9000,
              sha256: SHA2,
            },
          ],
          nextCursor: null,
        },
      });
    }
    if (action === 'verification' && method === 'POST') {
      return route.fulfill({
        json: {
          consistent: true,
          differences: [],
          integrity: p.bates['integrity'],
        },
      });
    }
    return undefined;
  }

  private exportRoute(
    route: Route,
    method: string,
    ws: string,
    body: Record<string, unknown> | null,
  ): Promise<void> | undefined {
    if (ws === 'exports' && method === 'GET') {
      const items = [...this.exports.values()].reverse();
      return route.fulfill({
        json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
      });
    }
    if (ws === 'exports' && method === 'POST') {
      const id = `exp-${this.next++}`;
      const e = exportResource(
        id,
        String(body?.['name'] ?? 'Export 2026-10-11'),
        String(body?.['snapshotId']),
        body ?? undefined,
      );
      this.exports.set(id, e);
      return route.fulfill({ status: 202, json: e });
    }
    const m = /^exports\/([^/]+)(?:\/(exclusions|files))?$/.exec(ws);
    const e = m ? this.exports.get(m[1]) : undefined;
    if (!m || !e) return route.fulfill({ status: 404, json: { title: 'Not found', status: 404 } });
    if (!m[2] && method === 'GET') return route.fulfill({ json: e });
    if (m[2] === 'exclusions') {
      return route.fulfill({
        json: {
          items: [
            {
              ordinal: 1,
              documentId: 'doc-7',
              controlNumber: 'DEMO-000007',
              reason: 'AccessChanged',
            },
            { ordinal: 2, documentId: 'doc-9', controlNumber: null, reason: 'AccessChanged' },
          ],
          nextCursor: null,
        },
      });
    }
    return route.fulfill({
      json: {
        items: [
          { fileId: 'ef-1', path: 'MANIFEST.csv', kind: 'manifest', sizeBytes: 20480, sha256: SHA },
          { fileId: 'ef-2', path: 'EXCLUSIONS.csv', kind: 'report', sizeBytes: 120, sha256: SHA2 },
          {
            fileId: 'ef-3',
            path: 'VOL001/DATA/VOL001.dat',
            kind: 'dat',
            sizeBytes: 64000,
            sha256: SHA2,
          },
        ],
        nextCursor: null,
      },
    });
  }
}
