import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  WorkspaceDeletionList,
  WorkspaceDeletionResource,
  WorkspaceDeletionWrite,
} from '../../core/api/generated/models';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  type DeletionView,
  type RetentionProfile,
  OPEN_DELETION_STATUSES,
  toDeletionView,
} from '../workspace-deletions/workspace-deletions-api';
import { LegalHoldApi } from './legal-hold-api';

/**
 * Workspace deletion as Admin › Workspace Settings sees it (E20-T02 / #167). `locked` while a legal hold applies (with
 * the oldest active hold's details for hold managers), `pending` while a request or run is open, `allowed` when the
 * caller may request deletion, `unavailable` (with the reason) otherwise. The request goes to
 * `POST …/workspaces/{id}/deletions`; nothing is removed until a second person approves it and the waiting period passes.
 */
export type DeletionAvailability =
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'locked'; readonly lock: PreservationLock }
  | { readonly kind: 'pending'; readonly deletion: DeletionView }
  | { readonly kind: 'allowed' };

/** A preservation lock (legal hold) on the workspace: while it exists nothing in it may be deleted. */
export interface PreservationLock {
  readonly placedBy: string;
  readonly placedAt: string;
  readonly reason: string;
}

export interface DeletionItem {
  readonly label: string;
  readonly detail?: string;
}

/** What a deletion removes and what its retention profile keeps (baseline §15, ADR-014 §5, Q-23). */
export interface DeletionSummary {
  readonly removed: readonly DeletionItem[];
  readonly retained: readonly DeletionItem[];
}

export const DELETION_SUMMARIES: Readonly<Record<RetentionProfile, DeletionSummary>> = {
  retainRecords: {
    removed: [
      { label: 'Documents', detail: 'metadata, coding and coding history' },
      { label: 'Stored files', detail: 'natives, extracted text and page images' },
      { label: 'Search index data' },
      { label: 'Fields, coding layouts, saved searches and views' },
      { label: 'Exports, reports and other generated files' },
    ],
    retained: [
      { label: 'Productions', detail: 'their documents list, Bates numbers and produced files' },
      { label: 'Audit trail', detail: 'including the record of this deletion' },
      { label: 'Destruction certificate' },
    ],
  },
  purgeAll: {
    removed: [
      { label: 'Documents', detail: 'metadata, coding and coding history' },
      { label: 'Stored files', detail: 'natives, extracted text and page images' },
      { label: 'Search index data' },
      { label: 'Fields, coding layouts, saved searches and views' },
      { label: 'Productions and exports', detail: 'with their produced files' },
      { label: 'Encryption keys', detail: 'backups of the workspace become unreadable' },
    ],
    retained: [
      { label: 'Audit trail', detail: 'including the record of this deletion' },
      { label: 'Destruction certificate' },
    ],
  },
};

/** What the requester submits from the Delete workspace dialog. */
export interface DeletionRequestForm {
  readonly retentionProfile: RetentionProfile;
  readonly reason: string;
  readonly externalReference: string | null;
  /** The workspace name exactly as typed; the API compares it too. */
  readonly confirmName: string;
}

/** Why the entry point is disabled for members without `Workspace.RequestDeletion`. */
export const DELETION_NEEDS_PERMISSION =
  'Only a Workspace Admin can request the deletion of this workspace.';

/** Why deletion is blocked when the caller cannot read the holds themselves. */
export const DELETION_BLOCKED_BY_HOLD =
  'Deletion is blocked while a legal hold (preservation lock) applies to this workspace.';

@Injectable()
export abstract class WorkspaceDeletion {
  abstract availability(): Promise<DeletionAvailability>;
  /** Submits a deletion request for approval; resolves with the recorded request. */
  abstract request(form: DeletionRequestForm): Promise<DeletionView>;
}

/**
 * The HTTP adapter. Reads the workspace afresh, so the answer follows a hold placed or released a moment ago, then the
 * workspace's deletions (`GET …/deletions`, `Workspace.RequestDeletion`).
 */
@Injectable()
export class HttpWorkspaceDeletion extends WorkspaceDeletion {
  private readonly http = inject(HttpClient);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly context = inject(WorkspaceContext);
  private readonly holds = inject(LegalHoldApi);

  async availability(): Promise<DeletionAvailability> {
    const workspace = await this.directory.get(this.context.workspaceId);
    if (Number(workspace.activePreservationLocks ?? 0) > 0) {
      if (!this.context.can(PERMISSIONS.manageHolds))
        return { kind: 'unavailable', reason: DELETION_BLOCKED_BY_HOLD };
      const active = (await this.holds.list()).items.filter((h) => h.status !== 'released');
      const oldest = active[active.length - 1];
      if (oldest)
        return {
          kind: 'locked',
          lock: {
            placedBy: oldest.placedBy.displayName || oldest.placedBy.userId,
            placedAt: String(oldest.placedAt),
            reason: oldest.reason,
          },
        };
    }
    if (!this.context.can(PERMISSIONS.requestDeletion))
      return { kind: 'unavailable', reason: DELETION_NEEDS_PERMISSION };
    const list = await firstValueFrom(
      this.http.get<WorkspaceDeletionList>(this.context.apiUrl('deletions')),
    );
    const open = list.items.find((d) => OPEN_DELETION_STATUSES.includes(d.status));
    return open ? { kind: 'pending', deletion: toDeletionView(open) } : { kind: 'allowed' };
  }

  async request(form: DeletionRequestForm): Promise<DeletionView> {
    const body: WorkspaceDeletionWrite = { ...form };
    return toDeletionView(
      await firstValueFrom(
        this.http.post<WorkspaceDeletionResource>(this.context.apiUrl('deletions'), body),
      ),
    );
  }
}
