import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  DeletionStepResource,
  WorkspaceDeletionApprovalWrite,
  WorkspaceDeletionList,
  WorkspaceDeletionResource,
  WorkspaceDeletionStatusResource,
} from '../../core/api/generated/models';

export type RetentionProfile = WorkspaceDeletionResource['retentionProfile'];
export type DeletionStatus = WorkspaceDeletionStatusResource;
export type DeletionStepName = DeletionStepResource;

/** Requested, approved, running or halted: the workspace has a deletion in progress. */
export const OPEN_DELETION_STATUSES: readonly DeletionStatus[] = [
  'requested',
  'approved',
  'running',
  'halted',
];

/** The run's steps in order (ADR-014 §4), with the words the progress list uses. */
export const DELETION_STEPS: readonly {
  readonly step: DeletionStepName;
  readonly label: string;
}[] = [
  { step: 'fence', label: 'Close the workspace to new work' },
  { step: 'drain', label: 'Stop and wait for running jobs' },
  { step: 'inventory', label: 'Count what the workspace holds' },
  { step: 'searchPurge', label: 'Remove search index data' },
  { step: 'databasePurge', label: 'Remove database records' },
  { step: 'storagePurge', label: 'Remove stored files' },
  { step: 'keyDestruction', label: 'Destroy encryption keys' },
  { step: 'verification', label: 'Check every store again' },
  { step: 'certification', label: 'Issue the destruction certificate' },
];

/** A deletion with its version as a number and dates as strings. */
export interface DeletionView extends Omit<WorkspaceDeletionResource, 'version'> {
  readonly version: number;
}

export function toDeletionView(resource: WorkspaceDeletionResource): DeletionView {
  return { ...resource, version: Number(resource.version) };
}

/**
 * Workspace deletions at installation level (E20-T02): `GET /api/v1/workspace-deletions` (every deletion for a
 * Retention Approver, the caller's own requests otherwise), one deletion with its progress, approve and cancel (with
 * the version as `If-Match`) and the destruction certificate download. They stay readable after the workspace itself
 * is fenced and gone. Approving needs `Installation.ApproveDeletion` and an MFA session (403 `step-up-required`), and
 * never by the requester (403 `second-person-required`).
 */
@Injectable({ providedIn: 'root', useFactory: () => new HttpWorkspaceDeletionsApi() })
export abstract class WorkspaceDeletionsApi {
  abstract list(openOnly?: boolean): Promise<DeletionView[]>;
  abstract get(deletionId: string): Promise<DeletionView>;
  abstract approve(deletion: DeletionView, note: string | null): Promise<DeletionView>;
  abstract cancel(deletion: DeletionView): Promise<DeletionView>;
  /** The certificate download (a JSON file with the certificate, its SHA-256 and signature). */
  abstract certificateUrl(deletionId: string): string;
}

const URL = '/api/v1/workspace-deletions';

export class HttpWorkspaceDeletionsApi extends WorkspaceDeletionsApi {
  private readonly http = inject(HttpClient);

  async list(openOnly = false): Promise<DeletionView[]> {
    const params: Record<string, string> = openOnly ? { openOnly: 'true' } : {};
    const body = await firstValueFrom(this.http.get<WorkspaceDeletionList>(URL, { params }));
    return body.items.map(toDeletionView);
  }

  async get(deletionId: string): Promise<DeletionView> {
    return toDeletionView(
      await firstValueFrom(
        this.http.get<WorkspaceDeletionResource>(`${URL}/${encodeURIComponent(deletionId)}`),
      ),
    );
  }

  approve(deletion: DeletionView, note: string | null): Promise<DeletionView> {
    const body: WorkspaceDeletionApprovalWrite = { note };
    return this.step(deletion, 'approve', body);
  }

  cancel(deletion: DeletionView): Promise<DeletionView> {
    return this.step(deletion, 'cancel', {});
  }

  certificateUrl(deletionId: string): string {
    return `${URL}/${encodeURIComponent(deletionId)}/certificate`;
  }

  private async step(deletion: DeletionView, action: string, body: unknown): Promise<DeletionView> {
    return toDeletionView(
      await firstValueFrom(
        this.http.post<WorkspaceDeletionResource>(
          `${URL}/${encodeURIComponent(deletion.deletionId)}/${action}`,
          body,
          { headers: { 'If-Match': `"${deletion.version}"` } },
        ),
      ),
    );
  }
}
