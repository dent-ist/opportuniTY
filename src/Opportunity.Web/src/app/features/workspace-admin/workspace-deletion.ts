import { Injectable } from '@angular/core';

/**
 * Workspace deletion as the settings page sees it. The backends are not built yet: the preservation lock (legal hold,
 * E20-T01 / #166) and defensible deletion (E20-T02 / #167). Until they are, `NotYetAvailableDeletion` answers
 * `unavailable` without calling any API, and the page shows the entry point disabled with that reason. When they land,
 * an HTTP adapter of this port answers `locked` (deletion blocked, with the lock's details) or `allowed` (with what the
 * deletion removes and keeps), and `DeleteWorkspaceDialog` already handles both.
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
  'Deleting a workspace is not available in this version. It arrives together with preservation locks (legal holds): a deletion will then be blocked while a lock applies, and otherwise needs a request approved by a second person.';

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
