import type { Route } from '@playwright/test';

/**
 * Search Terms Reports for the e2e mock API (#180), in the shapes of the wave-11 shared contract "Search Terms Reports"
 * (#72): the cursor-paged list, create (202 + job, Idempotency-Key), get (queued → running → completed over a few
 * reads), re-run on the same frozen set (same numbers), delete, the CSV/XLSX export through the gateway, the frozen
 * set (`GET …/snapshots/{id}`) and the report's job for the jobs API. `POST …/searches {searchTermReportId, termId}`
 * lists a term's hits (see mock-api.ts).
 */

interface Term {
  termId: string;
  name: string;
  expression: string;
  error: { code: string; message: string; position: number | null } | null;
  documentsWithHits: number | null;
  documentsWithHitsIncludingFamily: number | null;
  uniqueHits: number | null;
  uniqueHitsIncludingFamily: number | null;
}

interface Report {
  reportId: string;
  name: string;
  status: 'queued' | 'running' | 'completed' | 'failed';
  scope: { kind: string; id: string | null; name: string | null };
  termCount: number;
  createdBy: { userId: string; displayName: string };
  createdAt: string;
  completedAt: string | null;
  jobId: string;
  snapshotId: string | null;
  searchGeneration: number | null;
  indexCurrent: boolean | null;
  totals: {
    documentsInScope: number;
    documentsWithHits: number;
    documentsWithHitsIncludingFamily: number;
    documentsWithoutHits: number;
  } | null;
  terms: Term[];
  /** Reads left before the next state (queued → running → completed). */
  pending: number;
  /** The numbers the report has once it completes (a re-run restores the same ones). */
  final: Term[];
}

export interface TermReportRequest {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  idempotencyKey: string | null;
}

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };
const COLLEAGUE = { userId: 'user-2', displayName: 'Jamie Lee' };
const SCOPE_SIZE = 250;

/** A syntax error like the query parser reports: unbalanced quotes or parentheses, with the position. */
function syntaxError(expression: string): Term['error'] {
  const quotes = [...expression].filter((c) => c === '"').length;
  if (quotes % 2 === 1)
    return {
      code: 'unterminated-phrase',
      message: 'The phrase has no closing quote',
      position: expression.lastIndexOf('"'),
    };
  let depth = 0;
  for (let i = 0; i < expression.length; i++) {
    if (expression[i] === '(') depth++;
    if (expression[i] === ')' && --depth < 0)
      return { code: 'unexpected-token', message: 'Unexpected closing parenthesis', position: i };
  }
  if (depth > 0)
    return {
      code: 'unexpected-end',
      message: 'A closing parenthesis is missing',
      position: expression.length,
    };
  return null;
}

/** Deterministic counts per expression, so a re-run on the same set gives the same numbers. */
function counted(termId: string, name: string, expression: string): Term {
  const error = syntaxError(expression);
  if (error)
    return {
      termId,
      name,
      expression,
      error,
      documentsWithHits: null,
      documentsWithHitsIncludingFamily: null,
      uniqueHits: null,
      uniqueHitsIncludingFamily: null,
    };
  let h = 7;
  for (const c of expression) h = (h * 31 + c.charCodeAt(0)) % 9973;
  const hits = 5 + (h % 60);
  return {
    termId,
    name,
    expression,
    error: null,
    documentsWithHits: hits,
    documentsWithHitsIncludingFamily: hits + Math.floor(hits / 3),
    uniqueHits: Math.floor(hits / 4),
    uniqueHitsIncludingFamily: Math.floor(hits / 3),
  };
}

function totals(terms: readonly Term[]) {
  const withHits = Math.min(
    SCOPE_SIZE,
    terms.reduce((n, t) => n + (t.documentsWithHits ?? 0), 0) - 20,
  );
  return {
    documentsInScope: SCOPE_SIZE,
    documentsWithHits: Math.max(0, withHits),
    documentsWithHitsIncludingFamily: Math.min(SCOPE_SIZE, Math.max(0, withHits) + 30),
    documentsWithoutHits: SCOPE_SIZE - Math.max(0, withHits),
  };
}

function blank(t: Term): Term {
  return {
    ...t,
    documentsWithHits: null,
    documentsWithHitsIncludingFamily: null,
    uniqueHits: null,
    uniqueHitsIncludingFamily: null,
  };
}

function seeded(
  id: string,
  name: string,
  createdBy: { userId: string; displayName: string },
  scope: Report['scope'],
  terms: [string, string][],
  indexCurrent: boolean,
): Report {
  const final = terms.map(([n, e], i) => counted(`${id}-t${i + 1}`, n, e));
  return {
    reportId: id,
    name,
    status: 'completed',
    scope,
    termCount: final.length,
    createdBy,
    createdAt: '2026-10-03T10:40:00Z',
    completedAt: '2026-10-03T10:42:00Z',
    jobId: `job-${id}`,
    snapshotId: `snapshot-${id}`,
    searchGeneration: 18432,
    indexCurrent,
    totals: totals(final),
    terms: final,
    pending: 0,
    final,
  };
}

export class SearchTermReportsMock {
  readonly reports: Report[] = [
    seeded(
      'str-1',
      'Key terms – responsive emails',
      ME,
      { kind: 'savedSearch', id: 'ss-1', name: 'Responsive emails' },
      [
        ['Termination', 'terminat*'],
        ['Notice period', '"notice period" W/5 days'],
        ['Unbalanced phrase', '"breach of contract'],
        ['Penalty', 'penalt* OR liquidated'],
      ],
      true,
    ),
    seeded(
      'str-2',
      'Privilege screen',
      COLLEAGUE,
      { kind: 'workspace', id: null, name: null },
      [
        ['Counsel', 'counsel OR attorney'],
        ['Privileged', 'privileged'],
      ],
      false,
    ),
  ];
  readonly requests: TermReportRequest[] = [];
  private next = 1;

  /** A term of a report, or null when unknown (the search answers 404). */
  term(reportId: string, termId: string): Term | null {
    const r = this.reports.find((x) => x.reportId === reportId);
    return r?.final.find((t) => t.termId === termId) ?? null;
  }

  /** The report's job for `GET …/jobs/{id}` (the job monitor and the report page). */
  job(jobId: string): Record<string, unknown> | undefined {
    const r = this.reports.find((x) => x.jobId === jobId);
    if (!r) return undefined;
    const done = r.status === 'completed' ? 4 : r.status === 'running' ? 2 : 0;
    return {
      jobId,
      type: 'searchTermReport',
      name: r.name,
      status: r.status === 'queued' ? 'created' : r.status,
      createdBy: r.createdBy,
      createdAt: r.createdAt,
      updatedAt: new Date().toISOString(),
      completedAt: r.completedAt,
      committed: { done, total: 4 },
      searchable: { done: 0, total: 0, state: 'notApplicable' },
      errorCount: 0,
      correlationId: '00-5bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      snapshotId: r.snapshotId,
      link: `searches/terms-reports/${r.reportId}`,
      chunks: { pending: 4 - done, running: 0, done, failed: 0, deadLettered: 0, cancelled: 0 },
      attempts: 1,
      lastError: null,
      etaSeconds: r.status === 'completed' ? null : 20,
    };
  }

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const ws = /^\/api\/v1\/workspaces\/[^/]+\/(.+)$/.exec(path)?.[1];
    if (!ws) return undefined;
    const request = route.request();
    const json = (status: number, data: unknown) => route.fulfill({ status, json: data });
    const notFound = () =>
      route.fulfill({
        status: 404,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Not found', status: 404 }),
      });
    const record = () =>
      this.requests.push({
        method,
        path,
        body:
          method === 'POST' ? ((request.postDataJSON() ?? {}) as Record<string, unknown>) : null,
        idempotencyKey: request.headers()['idempotency-key'] ?? null,
      });

    const snapshot = /^snapshots\/(snapshot-str-[^/]+)$/.exec(ws);
    if (snapshot && method === 'GET') {
      const r = this.reports.find((x) => x.snapshotId === snapshot[1]);
      if (!r) return notFound();
      return json(200, {
        snapshotId: r.snapshotId,
        name: `Search Terms Report ${r.name}`,
        purpose: 'report',
        status: 'ready',
        documentCount: SCOPE_SIZE,
        selectedAt: '2026-10-03T10:41:00Z',
        materializedAt: '2026-10-03T10:41:02Z',
        createdBy: r.createdBy.userId,
        createdAt: '2026-10-03T10:41:00Z',
      });
    }

    if (ws === 'search-term-reports') {
      if (method === 'GET') {
        const items = [...this.reports]
          .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
          .map(summary);
        return json(200, { items, nextCursor: null });
      }
      if (method === 'POST') {
        record();
        const b = (request.postDataJSON() ?? {}) as {
          name?: string;
          terms?: { name: string; expression: string }[];
          scope?: { kind: string; id?: string };
        };
        const id = `str-new-${this.next++}`;
        const final = (b.terms ?? []).map((t, i) =>
          counted(`${id}-t${i + 1}`, t.name, t.expression),
        );
        const scopeKind = b.scope?.kind ?? 'workspace';
        const report: Report = {
          reportId: id,
          name: String(b.name ?? ''),
          status: 'queued',
          scope: {
            kind: scopeKind,
            id: b.scope?.id ?? null,
            name:
              scopeKind === 'savedSearch'
                ? b.scope?.id === 'ss-1'
                  ? 'Responsive emails'
                  : (b.scope?.id ?? null)
                : scopeKind === 'snapshot'
                  ? 'Mass Edit 2026-10-03'
                  : null,
          },
          termCount: final.length,
          createdBy: ME,
          createdAt: new Date().toISOString(),
          completedAt: null,
          jobId: `job-${id}`,
          snapshotId: null,
          searchGeneration: null,
          indexCurrent: null,
          totals: null,
          terms: final.map(blank),
          pending: 1,
          final,
        };
        this.reports.push(report);
        return json(202, resource(report));
      }
    }

    const item = /^search-term-reports\/([^/]+)(?:\/(rerun|export))?$/.exec(ws);
    if (!item) return undefined;
    const r = this.reports.find((x) => x.reportId === item[1]);
    if (!r) return notFound();
    const action = item[2];
    if (!action && method === 'GET') {
      advance(r);
      return json(200, resource(r));
    }
    if (!action && method === 'DELETE') {
      record();
      this.reports.splice(this.reports.indexOf(r), 1);
      return route.fulfill({ status: 204 });
    }
    if (action === 'rerun' && method === 'POST') {
      record();
      r.status = 'queued';
      r.completedAt = null;
      r.totals = null;
      r.terms = r.final.map(blank);
      r.pending = 1;
      return json(202, resource(r));
    }
    if (action === 'export' && method === 'GET') {
      const format = url.searchParams.get('format') === 'xlsx' ? 'xlsx' : 'csv';
      record();
      const csv = [
        'Term,Expression,Documents with hits,With family,Unique hits,Unique with family',
        ...r.final.map((t) =>
          [
            t.name,
            t.expression,
            t.documentsWithHits,
            t.documentsWithHitsIncludingFamily,
            t.uniqueHits,
            t.uniqueHitsIncludingFamily,
          ]
            .map((v) => `"${String(v ?? '').replaceAll('"', '""')}"`)
            .join(','),
        ),
      ].join('\r\n');
      return route.fulfill({
        status: 200,
        contentType:
          format === 'csv'
            ? 'text/csv; charset=utf-8'
            : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        headers: {
          'Content-Disposition': `attachment; filename="ACME_STR_${r.reportId}.${format}"`,
          'Cache-Control': 'no-store',
        },
        body: csv,
      });
    }
    return undefined;
  }
}

/** One read: a queued report starts running, a running one completes (with its frozen set). */
function advance(r: Report): void {
  if (r.status === 'completed' || r.status === 'failed') return;
  if (r.pending > 0) {
    r.pending--;
    return;
  }
  if (r.status === 'queued') {
    r.status = 'running';
    r.snapshotId = `snapshot-${r.reportId}`;
    r.pending = 1;
    return;
  }
  r.status = 'completed';
  r.completedAt = new Date().toISOString();
  r.searchGeneration = 18440;
  r.indexCurrent = true;
  r.terms = r.final;
  r.totals = totals(r.final);
}

function summary(r: Report) {
  return {
    reportId: r.reportId,
    name: r.name,
    status: r.status,
    scope: r.scope,
    termCount: r.termCount,
    createdBy: r.createdBy,
    createdAt: r.createdAt,
    completedAt: r.completedAt,
    jobId: r.jobId,
  };
}

function resource(r: Report) {
  return {
    ...summary(r),
    snapshotId: r.snapshotId,
    searchGeneration: r.searchGeneration,
    indexCurrent: r.indexCurrent,
    totals: r.totals,
    terms: r.terms,
    job: { jobId: r.jobId, status: r.status },
  };
}
