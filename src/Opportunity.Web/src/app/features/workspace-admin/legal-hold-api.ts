import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  PreservationLockList,
  PreservationLockReleaseWrite,
  PreservationLockResource,
  PreservationLockWrite,
} from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

/** A legal hold (`PreservationLockResource`) with its version as a number. */
export interface LegalHold extends Omit<PreservationLockResource, 'version'> {
  readonly version: number;
}

export interface LegalHolds {
  readonly items: readonly LegalHold[];
  readonly activeCount: number;
}

export interface PlaceHold {
  readonly reason: string;
  readonly matterReference: string | null;
  readonly releaseRequiresApproval: boolean;
}

/**
 * Legal holds (preservation locks, E20-T01): `…/preservation-locks` lists, places and releases them. A release sends
 * the hold's version as `If-Match`; when the hold needs a second person, the release is a request that another hold
 * manager approves (`…/release/approve`) or either cancels (`…/release/cancel`). Every call needs
 * `Workspace.ManageHolds`; 403 `second-person-required` means the caller requested the release themselves.
 */
@Injectable()
export abstract class LegalHoldApi {
  abstract list(): Promise<LegalHolds>;
  abstract place(hold: PlaceHold): Promise<LegalHold>;
  abstract release(hold: LegalHold, reason: string): Promise<LegalHold>;
  abstract approve(hold: LegalHold): Promise<LegalHold>;
  abstract cancel(hold: LegalHold): Promise<LegalHold>;
}

function normalise(resource: PreservationLockResource): LegalHold {
  return { ...resource, version: Number(resource.version) };
}

@Injectable()
export class HttpLegalHoldApi extends LegalHoldApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async list(): Promise<LegalHolds> {
    const body = await firstValueFrom(
      this.http.get<PreservationLockList>(this.context.apiUrl('preservation-locks')),
    );
    return { items: body.items.map(normalise), activeCount: Number(body.activeCount) };
  }

  async place(hold: PlaceHold): Promise<LegalHold> {
    const body: PreservationLockWrite = { ...hold };
    return normalise(
      await firstValueFrom(
        this.http.post<PreservationLockResource>(this.context.apiUrl('preservation-locks'), body),
      ),
    );
  }

  release(hold: LegalHold, reason: string): Promise<LegalHold> {
    const body: PreservationLockReleaseWrite = { reason };
    return this.step(hold, ['release'], body);
  }

  approve(hold: LegalHold): Promise<LegalHold> {
    return this.step(hold, ['release', 'approve'], {});
  }

  cancel(hold: LegalHold): Promise<LegalHold> {
    return this.step(hold, ['release', 'cancel'], {});
  }

  private async step(hold: LegalHold, path: readonly string[], body: unknown): Promise<LegalHold> {
    const url = this.context.apiUrl('preservation-locks', hold.lockId, ...path);
    return normalise(
      await firstValueFrom(
        this.http.post<PreservationLockResource>(url, body, {
          headers: { 'If-Match': `"${hold.version}"` },
        }),
      ),
    );
  }
}
