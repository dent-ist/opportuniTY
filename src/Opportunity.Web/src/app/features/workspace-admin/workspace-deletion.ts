import { Injectable, inject } from '@angular/core';
import { WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { LegalHoldApi } from './legal-hold-api';

/**
 * Workspace deletion as the settings page sees it. Legal holds (preservation locks, E20-T01 / #166) exist; defensible
 * deletion (E20-T02 / #167) does not yet. `HoldAwareDeletion` answers `locked` (with the hold's details) while a hold
 * applies and `unavailable` otherwise; `NotYetAvailableDeletion` answers `unavailable` without calling any API. When
 * #167 lands, its adapter answers `allowed` (with what the deletion removes and keeps) instead of `unavailable`, and
 * `DeleteWorkspaceDialog` already handles every case.
 */
export type DeletionAvailability =
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'locked'; readonly lock: PreservationLock }
  | { readonly kind: 'allowed'; readonly summary: DeletionSummary };

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

/** What a deletion removes and what the retention profile keeps (baseline §15, Q-23). */
export interface DeletionSummary {
  readonly removed: readonly DeletionItem[];
  readonly retained: readonly DeletionItem[];
  /** Q-23: a second person with the approval role approves the request before anything is removed. */
  readonly approvalRequired: boolean;
}

/** Baseline §15 (what a matter deletion removes) with the Q-23 default retention profile. */
export const DEFAULT_DELETION_SUMMARY: DeletionSummary = {
  removed: [
    { label: 'Documents', detail: 'metadata, coding and coding history' },
    { label: 'Stored files', detail: 'natives, extracted text and page images' },
    { label: 'Search index data' },
    { label: 'Fields, coding layouts, saved searches and views' },
    { label: 'Derived files', detail: 'exports and other generated files' },
  ],
  retained: [
    { label: 'Productions' },
    { label: 'Privilege logs' },
    { label: 'Audit trail', detail: 'including the record of this deletion' },
  ],
  approvalRequired: true,
};

/** Why the entry point is disabled in this version. */
export const DELETION_NOT_AVAILABLE =
  'Deleting a workspace is not available in this version. When it arrives, a deletion is blocked while a legal hold applies, and otherwise needs a request approved by a second person.';

/** Why deletion is blocked when the caller cannot read the holds themselves. */
export const DELETION_BLOCKED_BY_HOLD =
  'Deletion is blocked while a legal hold (preservation lock) applies to this workspace.';

@Injectable()
export abstract class WorkspaceDeletion {
  abstract availability(): Promise<DeletionAvailability>;
  /** Submits a deletion request for approval; `confirmedName` is the workspace name the user typed. */
  abstract request(confirmedName: string): Promise<void>;
}

/** This version: no deletion and no lock API exist, so nothing is called. */
@Injectable()
export class NotYetAvailableDeletion extends WorkspaceDeletion {
  availability(): Promise<DeletionAvailability> {
    return Promise.resolve({ kind: 'unavailable', reason: DELETION_NOT_AVAILABLE });
  }

  request(): Promise<void> {
    return Promise.reject(new Error(DELETION_NOT_AVAILABLE));
  }
}

/**
 * This version with legal holds: `locked` while a hold applies (the oldest active hold's details when the caller manages
 * holds), otherwise `unavailable` because deletion itself (#167) is not built. Reads the workspace afresh, so the answer
 * follows a hold placed or released a moment ago.
 */
@Injectable()
export class HoldAwareDeletion extends WorkspaceDeletion {
  private readonly directory = inject(WorkspaceDirectory);
  private readonly context = inject(WorkspaceContext);
  private readonly holds = inject(LegalHoldApi);

  async availability(): Promise<DeletionAvailability> {
    const workspace = await this.directory.get(this.context.workspaceId);
    if (Number(workspace.activePreservationLocks ?? 0) === 0)
      return { kind: 'unavailable', reason: DELETION_NOT_AVAILABLE };
    if (!this.context.can(PERMISSIONS.manageHolds))
      return { kind: 'unavailable', reason: DELETION_BLOCKED_BY_HOLD };
    const active = (await this.holds.list()).items.filter((h) => h.status !== 'released');
    const oldest = active[active.length - 1];
    if (!oldest) return { kind: 'unavailable', reason: DELETION_NOT_AVAILABLE };
    return {
      kind: 'locked',
      lock: {
        placedBy: oldest.placedBy.displayName || oldest.placedBy.userId,
        placedAt: String(oldest.placedAt),
        reason: oldest.reason,
      },
    };
  }

  request(): Promise<void> {
    return Promise.reject(new Error(DELETION_NOT_AVAILABLE));
  }
}
