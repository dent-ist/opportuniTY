import type { BadgeTone, IconName } from '../../ui';
import type {
  DeletionStatus,
  DeletionStepName,
  DeletionView,
  RetentionProfile,
} from './workspace-deletions-api';

export const STATUS_LABELS: Readonly<
  Record<DeletionStatus, { label: string; tone: BadgeTone; icon?: IconName }>
> = {
  requested: { label: 'Waiting for approval', tone: 'warning', icon: 'clock' },
  approved: { label: 'Approved', tone: 'info', icon: 'clock' },
  running: { label: 'Deleting', tone: 'info', icon: 'refresh' },
  halted: { label: 'Paused by a legal hold', tone: 'warning', icon: 'lock' },
  completed: { label: 'Deleted', tone: 'success', icon: 'check' },
  completedWithResiduals: { label: 'Deleted with residuals', tone: 'danger', icon: 'warning' },
  cancelled: { label: 'Cancelled', tone: 'neutral' },
  expired: { label: 'Expired', tone: 'neutral' },
};

export const PROFILE_LABELS: Readonly<Record<RetentionProfile, string>> = {
  retainRecords: 'Keep productions',
  purgeAll: 'Remove everything',
};

const n = (totals: Record<string, number | string>, key: string): number =>
  Number(totals[key] ?? 0);

/** One line about what a finished step did, from its totals (counts only). */
export function stepSummary(
  step: DeletionStepName,
  totals: Record<string, number | string>,
  format: (value: number) => string,
): string {
  switch (step) {
    case 'fence':
      return 'No new work is accepted for the workspace.';
    case 'drain':
      return `${format(n(totals, 'jobsCancelled'))} job(s) cancelled.`;
    case 'inventory':
      return `${format(n(totals, 'rows'))} records, ${format(n(totals, 'documents'))} indexed documents and ${format(n(totals, 'objects'))} stored files counted.`;
    case 'searchPurge':
      return `${format(n(totals, 'documentsDeleted'))} index entries removed.`;
    case 'databasePurge':
      return `${format(n(totals, 'rows'))} records removed.`;
    case 'storagePurge':
      return `${format(n(totals, 'objects'))} stored files removed.`;
    case 'keyDestruction':
      return n(totals, 'destroyed') > 0
        ? `${format(n(totals, 'destroyed'))} encryption key(s) destroyed.`
        : 'Encryption keys kept for the retained productions.';
    case 'verification':
      return n(totals, 'residualRows') > 0 || n(totals, 'documents') > 0 || n(totals, 'objects') > 0
        ? 'Data was still found after every attempt; see the certificate.'
        : 'Nothing of the workspace was found in any store.';
    case 'certification':
      return 'Destruction certificate issued.';
    default:
      return '';
  }
}

/** Where a deletion stands, for the list's "Next" column. */
export function nextStep(d: DeletionView, date: (value: unknown) => string): string {
  switch (d.status) {
    case 'requested':
      return `Approve before ${date(d.expiresAt)}`;
    case 'approved':
      return `Starts ${date(d.runNotBefore)}`;
    case 'running':
    case 'halted':
      return d.currentStep ? `Step ${stepNumber(d.currentStep)} of 9` : '';
    case 'completed':
    case 'completedWithResiduals':
      return `Finished ${date(d.finishedAt)}`;
    default:
      return '';
  }
}

const ORDER: readonly DeletionStepName[] = [
  'fence',
  'drain',
  'inventory',
  'searchPurge',
  'databasePurge',
  'storagePurge',
  'keyDestruction',
  'verification',
  'certification',
];

export function stepNumber(step: DeletionStepName): number {
  return ORDER.indexOf(step) + 1;
}
