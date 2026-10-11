import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  AcknowledgmentAcceptanceResource,
  AcknowledgmentAcceptanceWrite,
  AcknowledgmentResource,
  AcknowledgmentRosterEntryResource,
  AcknowledgmentVersionList,
  AcknowledgmentVersionResource,
  AcknowledgmentVersionWrite,
  CursorPageOfAcknowledgmentRosterEntryResource,
} from '../../core/api/generated/models';
import { workspaceUrl } from '../../core/workspace/workspace-api';

/** The member's view (`AcknowledgmentResource`) with the version as a number. */
export interface AcknowledgmentState extends Omit<AcknowledgmentResource, 'version'> {
  readonly version: number | null;
}

export interface AcknowledgmentVersion extends Omit<
  AcknowledgmentVersionResource,
  'version' | 'acceptedCount'
> {
  readonly version: number;
  readonly acceptedCount: number;
}

export interface AcknowledgmentVersions {
  readonly items: readonly AcknowledgmentVersion[];
  readonly currentVersion: number;
}

export interface RosterEntry extends Omit<AcknowledgmentRosterEntryResource, 'acceptances'> {
  readonly acceptances: readonly { version: number; textSha256: string; acceptedAt: string }[];
}

export interface RosterPage {
  readonly items: readonly RosterEntry[];
  readonly nextCursor: string | null;
  readonly total: number;
}

/**
 * Reviewer attestation and protective-order acknowledgment (E20-T03) over the API:
 *
 * - `GET …/acknowledgment` → the current text and whether the caller accepted it (reachable before accepting);
 * - `POST …/acknowledgment/acceptances` `{ version, textSha256 }` → 201 (200 when already accepted); 409
 *   `acknowledgment-outdated` when a newer version was published meanwhile;
 * - `GET/POST …/acknowledgment-versions` (`Workspace.ManageAcknowledgments`): the versions; publishing sends the
 *   current version as `If-Match` (`"0"` for the first) and answers 412 when someone published first;
 * - `GET …/acknowledgment-roster` (cursor pages) and `…/acknowledgment-roster/export` (CSV through the gateway, audited).
 */
@Injectable()
export abstract class AcknowledgmentApi {
  abstract state(workspaceId: string): Promise<AcknowledgmentState>;
  abstract accept(
    workspaceId: string,
    version: number,
    textSha256: string,
  ): Promise<AcknowledgmentAcceptanceResource>;
  abstract versions(workspaceId: string): Promise<AcknowledgmentVersions>;
  abstract version(workspaceId: string, version: number): Promise<AcknowledgmentVersion>;
  abstract publish(
    workspaceId: string,
    currentVersion: number,
    write: AcknowledgmentVersionWrite,
  ): Promise<AcknowledgmentVersion>;
  abstract roster(workspaceId: string, cursor?: string | null): Promise<RosterPage>;
  abstract exportUrl(workspaceId: string): string;
}

function version(resource: AcknowledgmentVersionResource): AcknowledgmentVersion {
  return {
    ...resource,
    version: Number(resource.version),
    acceptedCount: Number(resource.acceptedCount),
  };
}

@Injectable()
export class HttpAcknowledgmentApi extends AcknowledgmentApi {
  private readonly http = inject(HttpClient);

  private url(workspaceId: string, ...segments: string[]): string {
    return [workspaceUrl(workspaceId), ...segments.map(encodeURIComponent)].join('/');
  }

  async state(workspaceId: string): Promise<AcknowledgmentState> {
    const body = await firstValueFrom(
      this.http.get<AcknowledgmentResource>(this.url(workspaceId, 'acknowledgment')),
    );
    return { ...body, version: body.version === null ? null : Number(body.version) };
  }

  accept(
    workspaceId: string,
    versionNumber: number,
    textSha256: string,
  ): Promise<AcknowledgmentAcceptanceResource> {
    const body: AcknowledgmentAcceptanceWrite = { version: versionNumber, textSha256 };
    return firstValueFrom(
      this.http.post<AcknowledgmentAcceptanceResource>(
        this.url(workspaceId, 'acknowledgment', 'acceptances'),
        body,
      ),
    );
  }

  async versions(workspaceId: string): Promise<AcknowledgmentVersions> {
    const body = await firstValueFrom(
      this.http.get<AcknowledgmentVersionList>(this.url(workspaceId, 'acknowledgment-versions')),
    );
    return { items: body.items.map(version), currentVersion: Number(body.currentVersion) };
  }

  async version(workspaceId: string, versionNumber: number): Promise<AcknowledgmentVersion> {
    return version(
      await firstValueFrom(
        this.http.get<AcknowledgmentVersionResource>(
          this.url(workspaceId, 'acknowledgment-versions', String(versionNumber)),
        ),
      ),
    );
  }

  async publish(
    workspaceId: string,
    currentVersion: number,
    write: AcknowledgmentVersionWrite,
  ): Promise<AcknowledgmentVersion> {
    return version(
      await firstValueFrom(
        this.http.post<AcknowledgmentVersionResource>(
          this.url(workspaceId, 'acknowledgment-versions'),
          write,
          { headers: { 'If-Match': `"${currentVersion}"` } },
        ),
      ),
    );
  }

  async roster(workspaceId: string, cursor?: string | null): Promise<RosterPage> {
    const params: Record<string, string> = { limit: '100' };
    if (cursor) params['cursor'] = cursor;
    const body = await firstValueFrom(
      this.http.get<CursorPageOfAcknowledgmentRosterEntryResource>(
        this.url(workspaceId, 'acknowledgment-roster'),
        { params },
      ),
    );
    return {
      items: body.items.map((e) => ({
        ...e,
        acceptances: e.acceptances.map((a) => ({
          version: Number(a.version),
          textSha256: a.textSha256,
          acceptedAt: String(a.acceptedAt),
        })),
      })),
      nextCursor: body.nextCursor ?? null,
      total: Number(body.total.value),
    };
  }

  exportUrl(workspaceId: string): string {
    return this.url(workspaceId, 'acknowledgment-roster', 'export');
  }
}
