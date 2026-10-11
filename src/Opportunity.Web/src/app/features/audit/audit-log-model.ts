import type {
  AuditEventResource,
  AuditExportResource,
  AuditOutcomeResource,
} from '../../core/api/generated/models';
import { dayStart } from '../../core/jobs/job-model';
import type { JobStatus, SelectOption } from '../../ui';

/**
 * The audit log viewer's filters, as kept in the URL (all strings; empty = not set). They combine: each one narrows
 * the list. Dates are local calendar days (`yyyy-mm-dd`), both inclusive.
 */
export interface AuditFilter {
  readonly from: string;
  readonly to: string;
  readonly category: string;
  readonly action: string;
  /** A user ID (set from a row's user, shown with `actorName`). */
  readonly actor: string;
  readonly actorName: string;
  readonly controlNumber: string;
  readonly job: string;
  readonly outcome: string;
  /** `'1'` shows search text (needs `Audit.ReadSearchText`). */
  readonly searchText: string;
}

export const EMPTY_FILTER: AuditFilter = {
  from: '',
  to: '',
  category: '',
  action: '',
  actor: '',
  actorName: '',
  controlNumber: '',
  job: '',
  outcome: '',
  searchText: '',
};

/** The URL query parameter of each filter. */
export const FILTER_PARAMS: Readonly<Record<keyof AuditFilter, string>> = {
  from: 'from',
  to: 'to',
  category: 'category',
  action: 'action',
  actor: 'user',
  actorName: 'userName',
  controlNumber: 'document',
  job: 'job',
  outcome: 'outcome',
  searchText: 'searchText',
};

export function isFiltered(filter: AuditFilter): boolean {
  return (Object.keys(EMPTY_FILTER) as (keyof AuditFilter)[]).some(
    (k) => k !== 'actorName' && k !== 'searchText' && !!filter[k],
  );
}

export interface AuditQuery {
  readonly from?: string;
  readonly to?: string;
  readonly category?: string;
  readonly action?: string;
  readonly actorId?: string;
  readonly controlNumber?: string;
  readonly jobId?: string;
  readonly outcome?: AuditOutcomeResource;
  readonly includeSearchText?: boolean;
}

/** The API's query (and export request) for the filters: local days become an instant range, `to` exclusive. */
export function toQuery(filter: AuditFilter): AuditQuery {
  const query: { -readonly [K in keyof AuditQuery]: AuditQuery[K] } = {};
  const from = dayStart(filter.from);
  const to = dayStart(nextDay(filter.to));
  if (from) query.from = from;
  if (to) query.to = to;
  if (filter.category) query.category = filter.category;
  if (filter.category && filter.action) query.action = filter.action;
  if (filter.actor) query.actorId = filter.actor;
  if (filter.controlNumber.trim()) query.controlNumber = filter.controlNumber.trim();
  if (filter.job.trim()) query.jobId = filter.job.trim();
  if (isOutcome(filter.outcome)) query.outcome = filter.outcome;
  if (filter.searchText === '1') query.includeSearchText = true;
  return query;
}

function nextDay(day: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!m) return '';
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]) + 1);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

const OUTCOMES: readonly AuditOutcomeResource[] = ['success', 'denied', 'failure'];

function isOutcome(value: string): value is AuditOutcomeResource {
  return (OUTCOMES as readonly string[]).includes(value);
}

export const OUTCOME_OPTIONS: readonly SelectOption[] = [
  { value: 'success', label: 'Succeeded' },
  { value: 'denied', label: 'Denied' },
  { value: 'failure', label: 'Failed' },
];

export function outcomeLabel(outcome: AuditOutcomeResource): string {
  return OUTCOME_OPTIONS.find((o) => o.value === outcome)?.label ?? outcome;
}

/** `ResultsPageServed` → "Results page served", `SavedSearch.Created` → "Saved search created". */
export function actionLabel(action: string): string {
  const words = action
    .replace(/\./g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Category names as shown in the filter (the taxonomy's own names, with the abbreviations spelled out). */
export function categoryLabel(category: string): string {
  return category === 'AuthZ' ? 'Access denied' : category === 'Auth' ? 'Sign-in' : category;
}

/** What the event is about: a document by its Control Number when the reader may see it, else type and ID. */
export function resourceText(event: AuditEventResource): string {
  const resource = event.resource;
  if (!resource) return '';
  if (resource.controlNumber) return resource.controlNumber;
  const type = resource.type.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  return resource.id ? `${type} ${shortId(resource.id)}` : type;
}

/** The first 8 characters of an ID (the full ID is in the row's details). */
export function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 8)}…` : id;
}

/** Details as label/value pairs, in a stable order. */
export function detailRows(event: AuditEventResource): { label: string; value: string }[] {
  return Object.entries(event.details ?? {})
    .filter(([, v]) => v !== null && v !== undefined && v !== '')
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([label, value]) => ({ label: actionLabel(label), value: String(value) }));
}

export function exportStatus(e: AuditExportResource): JobStatus {
  switch (e.status) {
    case 'completed':
      return 'succeeded';
    case 'failed':
      return 'failed';
    case 'running':
      return 'running';
    default:
      return 'queued';
  }
}

export function isActiveExport(e: AuditExportResource): boolean {
  return e.status === 'queued' || e.status === 'running';
}
