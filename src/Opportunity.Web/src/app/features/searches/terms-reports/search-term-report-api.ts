import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { getSnapshot } from '../../../core/api/generated/fn/snapshots/get-snapshot';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// Search Terms Reports (E07-T10 backend #72, screen #180) as a port. Written by hand against the wave-11 shared
// contract "Search Terms Reports" while the API is built in parallel; the e2e mock API
// (e2e/support/mock-search-term-reports.ts) serves the same shapes. Readers are tolerant (int64 as strings, missing
// fields), like the other hand-written ports. Provided by the pages that use it, so it is workspace-scoped.

export type ReportStatus = 'queued' | 'running' | 'completed' | 'failed';
export type ReportScopeKind = 'workspace' | 'savedSearch' | 'snapshot';
export type ExportFormat = 'csv' | 'xlsx';

export interface ReportScope {
  readonly kind: ReportScopeKind;
  readonly id: string | null;
  readonly name: string | null;
}

export interface ReportUser {
  readonly userId: string;
  readonly displayName: string;
}

/** `GET …/search-term-reports` item. */
export interface SearchTermReportSummary {
  readonly reportId: string;
  readonly name: string;
  readonly status: ReportStatus | string;
  readonly scope: ReportScope;
  readonly termCount: number;
  readonly createdBy: ReportUser;
  readonly createdAt: string;
  readonly completedAt: string | null;
  readonly jobId: string | null;
}

/** A term that could not be parsed: its error, with the 0-based character position when known. */
export interface TermError {
  readonly code: string;
  readonly message: string;
  readonly position: number | null;
}

export interface ReportTerm {
  readonly termId: string;
  readonly name: string;
  readonly expression: string;
  readonly error: TermError | null;
  readonly documentsWithHits: number | null;
  readonly documentsWithHitsIncludingFamily: number | null;
  readonly uniqueHits: number | null;
  readonly uniqueHitsIncludingFamily: number | null;
}

export interface ReportTotals {
  readonly documentsInScope: number | null;
  readonly documentsWithHits: number | null;
  readonly documentsWithHitsIncludingFamily: number | null;
  readonly documentsWithoutHits: number | null;
}

/** `GET …/search-term-reports/{reportId}`. Counts are null until the report has completed. */
export interface SearchTermReport extends SearchTermReportSummary {
  readonly snapshotId: string | null;
  readonly searchGeneration: string | null;
  /** False: the search index was not current when the report ran. Null: not known (yet). */
  readonly indexCurrent: boolean | null;
  readonly totals: ReportTotals | null;
  readonly terms: readonly ReportTerm[];
}

export interface TermDraft {
  readonly name: string;
  readonly expression: string;
}

/** Body of `POST …/search-term-reports`. */
export interface SearchTermReportDraft {
  readonly name: string;
  readonly terms: readonly TermDraft[];
  readonly scope: { readonly kind: ReportScopeKind; readonly id?: string };
}

export interface ReportPage {
  readonly items: readonly SearchTermReportSummary[];
  readonly nextCursor: string | null;
}

/** The frozen set a report counted, as far as the snapshot API tells it (`GET …/snapshots/{id}`). */
export interface ReportSnapshot {
  readonly snapshotId: string;
  readonly documentCount: number | null;
  /** When the documents were selected (frozen). */
  readonly frozenAt: string | null;
}

@Injectable()
export abstract class SearchTermReportApi {
  /** `GET …/search-term-reports?cursor=&limit=`, newest first. */
  abstract list(cursor?: string | null, limit?: number): Promise<ReportPage>;
  /** `GET …/search-term-reports/{reportId}`; 404 when it does not exist or is not visible to the caller. */
  abstract get(reportId: string): Promise<SearchTermReport>;
  /** `POST …/search-term-reports` with Idempotency-Key: 202 and the report with its job. */
  abstract create(draft: SearchTermReportDraft, idempotencyKey: string): Promise<SearchTermReport>;
  /** `POST …/search-term-reports/{reportId}/rerun` with Idempotency-Key: 202, same frozen set, same numbers. */
  abstract rerun(reportId: string, idempotencyKey: string): Promise<SearchTermReport | null>;
  /** `DELETE …/search-term-reports/{reportId}` (the person who ran it, or an admin). */
  abstract delete(reportId: string): Promise<void>;
  /** `GET …/search-term-reports/{reportId}/export?format=` (protected-content gateway download, audited). */
  abstract exportUrl(reportId: string, format: ExportFormat): string;
  /** `GET …/snapshots/{snapshotId}`: when the frozen set was taken. */
  abstract snapshot(snapshotId: string): Promise<ReportSnapshot>;
}

const REPORTS = 'search-term-reports';

@Injectable()
export class HttpSearchTermReportApi extends SearchTermReportApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  list(cursor?: string | null, limit = 50): Promise<ReportPage> {
    const params: Record<string, string> = { limit: String(limit) };
    if (cursor) params['cursor'] = cursor;
    return firstValueFrom(
      this.http
        .get<{
          items?: unknown[];
          nextCursor?: string | null;
        }>(this.context.apiUrl(REPORTS), { params })
        .pipe(
          map((body) => ({
            items: (body?.items ?? []).map(toReportSummary),
            nextCursor: body?.nextCursor ?? null,
          })),
        ),
    );
  }

  get(reportId: string): Promise<SearchTermReport> {
    return firstValueFrom(
      this.http.get<unknown>(this.context.apiUrl(REPORTS, reportId)).pipe(map(toReport)),
    );
  }

  create(draft: SearchTermReportDraft, idempotencyKey: string): Promise<SearchTermReport> {
    return firstValueFrom(
      this.http
        .post<unknown>(this.context.apiUrl(REPORTS), draft, {
          headers: { 'Idempotency-Key': idempotencyKey },
        })
        .pipe(map(toReport)),
    );
  }

  rerun(reportId: string, idempotencyKey: string): Promise<SearchTermReport | null> {
    return firstValueFrom(
      this.http
        .post<unknown>(
          this.context.apiUrl(REPORTS, reportId, 'rerun'),
          {},
          { headers: { 'Idempotency-Key': idempotencyKey } },
        )
        // A 202 without a body: the caller re-reads the report.
        .pipe(map((body) => (body && typeof body === 'object' ? toReport(body) : null))),
    );
  }

  async delete(reportId: string): Promise<void> {
    await firstValueFrom(this.http.delete(this.context.apiUrl(REPORTS, reportId)));
  }

  exportUrl(reportId: string, format: ExportFormat): string {
    return `${this.context.apiUrl(REPORTS, reportId, 'export')}?format=${format}`;
  }

  async snapshot(snapshotId: string): Promise<ReportSnapshot> {
    const s = await firstValueFrom(
      getSnapshot(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        snapshotId,
      }).pipe(map((r) => r.body)),
    );
    return {
      snapshotId: s.snapshotId,
      documentCount: num(s.documentCount),
      frozenAt: (s.selectedAt ?? s.materializedAt ?? s.createdAt ?? null) as string | null,
    };
  }
}

// ── Tolerant readers ────────────────────────────────────────────────────────────────────────────────────────

type Raw = Record<string, unknown>;
const obj = (v: unknown): Raw => (v && typeof v === 'object' ? (v as Raw) : {});
const str = (v: unknown): string | null =>
  v === null || v === undefined || v === '' ? null : String(v);

/** The API may send int64 values as strings; the UI works with numbers. */
export const num = (value: unknown): number | null => {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
};

const SCOPES = new Set<string>(['workspace', 'savedSearch', 'snapshot']);

function toScope(body: unknown): ReportScope {
  const raw = obj(body);
  const kind = String(raw['kind'] ?? 'workspace');
  return {
    kind: (SCOPES.has(kind) ? kind : 'workspace') as ReportScopeKind,
    id: str(raw['id']),
    name: str(raw['name']),
  };
}

function toUser(body: unknown): ReportUser {
  const raw = obj(body);
  return {
    userId: String(raw['userId'] ?? ''),
    displayName: String(raw['displayName'] ?? raw['userId'] ?? ''),
  };
}

export function toReportSummary(body: unknown): SearchTermReportSummary {
  const raw = obj(body);
  return {
    reportId: String(raw['reportId'] ?? ''),
    name: String(raw['name'] ?? ''),
    status: String(raw['status'] ?? 'queued'),
    scope: toScope(raw['scope']),
    termCount: num(raw['termCount']) ?? 0,
    createdBy: toUser(raw['createdBy']),
    createdAt: String(raw['createdAt'] ?? ''),
    completedAt: str(raw['completedAt']),
    jobId: str(raw['jobId'] ?? obj(raw['job'])['jobId']),
  };
}

function toTermError(body: unknown): TermError | null {
  if (!body || typeof body !== 'object') return null;
  const raw = obj(body);
  return {
    code: String(raw['code'] ?? 'invalid'),
    message: String(raw['message'] ?? 'This term could not be read.'),
    position: num(raw['position']),
  };
}

function toTerm(body: unknown): ReportTerm {
  const raw = obj(body);
  return {
    termId: String(raw['termId'] ?? ''),
    name: String(raw['name'] ?? ''),
    expression: String(raw['expression'] ?? ''),
    error: toTermError(raw['error']),
    documentsWithHits: num(raw['documentsWithHits']),
    documentsWithHitsIncludingFamily: num(raw['documentsWithHitsIncludingFamily']),
    uniqueHits: num(raw['uniqueHits']),
    uniqueHitsIncludingFamily: num(raw['uniqueHitsIncludingFamily']),
  };
}

function toTotals(body: unknown): ReportTotals | null {
  if (!body || typeof body !== 'object') return null;
  const raw = obj(body);
  return {
    documentsInScope: num(raw['documentsInScope']),
    documentsWithHits: num(raw['documentsWithHits']),
    documentsWithHitsIncludingFamily: num(raw['documentsWithHitsIncludingFamily']),
    documentsWithoutHits: num(raw['documentsWithoutHits']),
  };
}

export function toReport(body: unknown): SearchTermReport {
  const raw = obj(body);
  const index = raw['indexCurrent'];
  return {
    ...toReportSummary(body),
    snapshotId: str(raw['snapshotId']),
    searchGeneration: str(raw['searchGeneration']),
    indexCurrent: typeof index === 'boolean' ? index : null,
    totals: toTotals(raw['totals']),
    terms: Array.isArray(raw['terms']) ? raw['terms'].map(toTerm) : [],
  };
}
