import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  AccessReviewEntryResource,
  AccessReviewResource,
  WallProofResource,
} from '../../core/api/generated/models';
import { workspaceUrl } from '../../core/workspace/workspace-api';

/** A report period: ISO-8601 instants, `from` inclusive and `to` exclusive; either may be left to the server default. */
export interface CompliancePeriod {
  readonly from?: string | null;
  readonly to?: string | null;
}

export type WallProof = WallProofResource;

export interface AccessReviewEntry extends Omit<AccessReviewEntryResource, 'knownMembers'> {
  readonly knownMembers: number | null;
}

export interface AccessReview extends Omit<AccessReviewResource, 'entries' | 'users' | 'groups'> {
  readonly users: number;
  readonly groups: number;
  readonly entries: readonly AccessReviewEntry[];
}

/**
 * Compliance reports (E14-T06) over the API. Every generation is audited by the server:
 *
 * - `GET …/compliance/wall-proof?from&to` (JSON) and `…/wall-proof/export` (CSV through the gateway): every audited attempt
 *   by a walled user on walled content in the period, and whether the walls held (`Compliance.Report`);
 * - `GET …/compliance/access-review` (JSON) and `…/access-review/export` (CSV): users and groups with roles, walls, last
 *   workspace access and last sign-in (`Compliance.Report`);
 * - `GET …/compliance/audit-export?from&to` (JSON Lines through the gateway): the verifiable audit export (`Audit.Export`).
 */
@Injectable()
export abstract class ComplianceApi {
  abstract wallProof(workspaceId: string, period: CompliancePeriod): Promise<WallProof>;
  abstract accessReview(workspaceId: string): Promise<AccessReview>;
  abstract wallProofExportUrl(workspaceId: string, period: CompliancePeriod): string;
  abstract accessReviewExportUrl(workspaceId: string): string;
  abstract auditExportUrl(workspaceId: string, period: CompliancePeriod): string;
}

function query(period: CompliancePeriod): string {
  const params = new URLSearchParams();
  if (period.from) params.set('from', period.from);
  if (period.to) params.set('to', period.to);
  const text = params.toString();
  return text ? `?${text}` : '';
}

@Injectable()
export class HttpComplianceApi extends ComplianceApi {
  private readonly http = inject(HttpClient);

  private url(workspaceId: string, ...segments: string[]): string {
    return [workspaceUrl(workspaceId), 'compliance', ...segments.map(encodeURIComponent)].join('/');
  }

  wallProof(workspaceId: string, period: CompliancePeriod): Promise<WallProof> {
    const params: Record<string, string> = {};
    if (period.from) params['from'] = period.from;
    if (period.to) params['to'] = period.to;
    return firstValueFrom(
      this.http.get<WallProofResource>(this.url(workspaceId, 'wall-proof'), { params }),
    );
  }

  async accessReview(workspaceId: string): Promise<AccessReview> {
    const body = await firstValueFrom(
      this.http.get<AccessReviewResource>(this.url(workspaceId, 'access-review')),
    );
    return {
      ...body,
      users: Number(body.users),
      groups: Number(body.groups),
      entries: body.entries.map((e) => ({
        ...e,
        knownMembers: e.knownMembers === null ? null : Number(e.knownMembers),
      })),
    };
  }

  wallProofExportUrl(workspaceId: string, period: CompliancePeriod): string {
    return this.url(workspaceId, 'wall-proof', 'export') + query(period);
  }

  accessReviewExportUrl(workspaceId: string): string {
    return this.url(workspaceId, 'access-review', 'export');
  }

  auditExportUrl(workspaceId: string, period: CompliancePeriod): string {
    return this.url(workspaceId, 'audit-export') + query(period);
  }
}

/** Start of a local `yyyy-mm-dd` day as ISO-8601; with `nextDay`, the start of the following day (an exclusive end). */
export function dayBoundary(day: string, nextDay = false): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!match) return null;
  const d = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + (nextDay ? 1 : 0));
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}
