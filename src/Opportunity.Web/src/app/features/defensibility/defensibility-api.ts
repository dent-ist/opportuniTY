import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  ChainOfCustodyResource,
  ProductionHistoryResource,
  ReviewSummaryResource,
  SearchTermHitsResource,
} from '../../core/api/generated/models';
import { workspaceUrl } from '../../core/workspace/workspace-api';

export type ReviewSummary = ReviewSummaryResource;
export type ProductionHistory = ProductionHistoryResource;
export type SearchTermHits = SearchTermHitsResource;
export type ChainOfCustody = ChainOfCustodyResource;

/** What the reports are generated for: `asOf` (ISO 8601, empty for now) and, for the review summary, a snapshot. */
export interface DefensibilityQuery {
  readonly asOf?: string | null;
  readonly snapshotId?: string | null;
}

/** The report pack: the reports' point, the start of the wall-proof and audit-trail period, and documents to trace. */
export interface PackQuery extends DefensibilityQuery {
  readonly from?: string | null;
  readonly documentIds?: readonly string[];
}

/**
 * Defensibility reports (E14-T05) over the API. Every generation is audited by the server, so reports are requested
 * only when the user asks:
 *
 * - `GET …/defensibility/review-summary`, `…/production-history`, `…/search-term-hits` (JSON), each with `/export` (CSV
 *   through the gateway);
 * - `GET …/documents/{id}/chain-of-custody` (JSON, re-reads the stored bytes) and `/export` (CSV);
 * - `GET …/defensibility/pack` (ZIP with every report, production manifests, QC reports, compliance reports and the
 *   audit trail).
 *
 * All need `Compliance.Report`; each takes an optional `asOf` and states it with a fingerprint.
 */
@Injectable()
export abstract class DefensibilityApi {
  abstract reviewSummary(workspaceId: string, query: DefensibilityQuery): Promise<ReviewSummary>;
  abstract productionHistory(
    workspaceId: string,
    query: DefensibilityQuery,
  ): Promise<ProductionHistory>;
  abstract searchTermHits(workspaceId: string, query: DefensibilityQuery): Promise<SearchTermHits>;
  abstract chainOfCustody(
    workspaceId: string,
    documentId: string,
    query: DefensibilityQuery,
  ): Promise<ChainOfCustody>;
  abstract exportUrl(
    workspaceId: string,
    report: 'review-summary' | 'production-history' | 'search-term-hits',
    query: DefensibilityQuery,
  ): string;
  abstract chainOfCustodyExportUrl(
    workspaceId: string,
    documentId: string,
    query: DefensibilityQuery,
  ): string;
  abstract packUrl(workspaceId: string, query: PackQuery): string;
}

function params(query: PackQuery): URLSearchParams {
  const p = new URLSearchParams();
  if (query.asOf) p.set('asOf', query.asOf);
  if (query.snapshotId) p.set('snapshotId', query.snapshotId);
  if (query.from) p.set('from', query.from);
  for (const id of query.documentIds ?? []) p.append('documentId', id);
  return p;
}

function withQuery(url: string, query: PackQuery): string {
  const text = params(query).toString();
  return text ? `${url}?${text}` : url;
}

@Injectable()
export class HttpDefensibilityApi extends DefensibilityApi {
  private readonly http = inject(HttpClient);

  private url(workspaceId: string, ...segments: string[]): string {
    return [workspaceUrl(workspaceId), 'defensibility', ...segments.map(encodeURIComponent)].join(
      '/',
    );
  }

  private custodyUrl(workspaceId: string, documentId: string): string {
    return `${workspaceUrl(workspaceId)}/documents/${encodeURIComponent(documentId)}/chain-of-custody`;
  }

  private get<T>(url: string, query: DefensibilityQuery): Promise<T> {
    return firstValueFrom(this.http.get<T>(withQuery(url, query)));
  }

  reviewSummary(workspaceId: string, query: DefensibilityQuery): Promise<ReviewSummary> {
    return this.get(this.url(workspaceId, 'review-summary'), query);
  }

  productionHistory(workspaceId: string, query: DefensibilityQuery): Promise<ProductionHistory> {
    return this.get(this.url(workspaceId, 'production-history'), { asOf: query.asOf });
  }

  searchTermHits(workspaceId: string, query: DefensibilityQuery): Promise<SearchTermHits> {
    return this.get(this.url(workspaceId, 'search-term-hits'), { asOf: query.asOf });
  }

  chainOfCustody(
    workspaceId: string,
    documentId: string,
    query: DefensibilityQuery,
  ): Promise<ChainOfCustody> {
    return this.get(this.custodyUrl(workspaceId, documentId), { asOf: query.asOf });
  }

  exportUrl(
    workspaceId: string,
    report: 'review-summary' | 'production-history' | 'search-term-hits',
    query: DefensibilityQuery,
  ): string {
    return withQuery(
      this.url(workspaceId, report, 'export'),
      report === 'review-summary' ? query : { asOf: query.asOf },
    );
  }

  chainOfCustodyExportUrl(
    workspaceId: string,
    documentId: string,
    query: DefensibilityQuery,
  ): string {
    return withQuery(`${this.custodyUrl(workspaceId, documentId)}/export`, { asOf: query.asOf });
  }

  packUrl(workspaceId: string, query: PackQuery): string {
    return withQuery(this.url(workspaceId, 'pack'), query);
  }
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Document IDs from free text (one per line, or separated by commas or spaces); null when one is not an ID. */
export function parseDocumentIds(text: string): string[] | null {
  const ids = text
    .split(/[\s,;]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
  return ids.every((id) => GUID.test(id)) ? [...new Set(ids.map((id) => id.toLowerCase()))] : null;
}

/** A `datetime-local` value (local time) as ISO 8601 UTC; null when empty or invalid. */
export function localDateTime(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}
