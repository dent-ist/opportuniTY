import type {
  ReviewBatchResource,
  ReviewBatchSortResource,
  ReviewBatchStatusResource,
} from '../../core/api/generated/models';
import type { ApiError } from '../../core/api/problem-details';
import type { BadgeTone, IconName } from '../../ui';

/** `?batch=<batchId>` on Documents: the list (and Review mode) bound to one review batch (#178). */
export const BATCH_PARAM = 'batch';

/** Batch status as reviewers read it (#178 acceptance criteria): Pending → In progress → Completed. */
export const BATCH_STATUS: Readonly<
  Record<ReviewBatchStatusResource, { label: string; tone: BadgeTone; icon: IconName }>
> = {
  available: { label: 'Pending', tone: 'neutral', icon: 'clock' },
  checkedOut: { label: 'In progress', tone: 'info', icon: 'pencil' },
  completed: { label: 'Completed', tone: 'success', icon: 'success' },
};

export const SORT_LABEL: Readonly<Record<ReviewBatchSortResource, string>> = {
  controlNumber: 'Control Number',
  familyDate: 'Family Date',
  documentDate: 'Document Date',
};

/** How a Batch Set ordered its documents before cutting them into batches. */
export function orderText(sort: ReviewBatchSortResource, descending: boolean): string {
  if (sort === 'controlNumber') return 'Control Number (families by their parent)';
  return `${SORT_LABEL[sort]}, ${descending ? 'latest' : 'earliest'} first; undated documents last`;
}

/** The batch's holder: "You", their name, or "—" when it is pending. */
export function assigneeText(batch: ReviewBatchResource, myId: string | null): string {
  if (!batch.assignee) return '—';
  if (batch.assignee.userId === myId) return 'You';
  return batch.assignee.displayName ?? 'Unknown user';
}

/** Plain-language reasons for the batch actions' refusals; null for anything the error state explains. */
export function batchErrorText(error: ApiError): string | null {
  switch (error.status) {
    case 409:
      return error.problem.detail ?? 'The batch is no longer in a state that allows this.';
    case 412:
      return 'Someone changed this batch meanwhile. The list shows its current state; try again.';
    case 403:
      return error.problem.detail ?? 'You do not have permission for this batch.';
    case 404:
      return 'This batch or Batch Set no longer exists, or you cannot see it.';
    default:
      return null;
  }
}
