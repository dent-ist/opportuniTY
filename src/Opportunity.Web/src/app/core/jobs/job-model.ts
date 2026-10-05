import type { JobStatus as PillStatus, ToastTone } from '../../ui';

// Jobs (E06-T07) as the wave-9 "Job operations" contract (#61) describes them, plus the pure rules of the Jobs page,
// the job detail and the job tray: wording, two-phase progress ("Saved" / "Searchable", ticket review E16-T07),
// permissions and the merge of live updates. No Angular here, so every rule is unit-tested.

/** `JobSummary.status`; the API may add states, which then show as their raw name. */
export type JobState =
  | 'created'
  | 'preparing'
  | 'running'
  | 'paused'
  | 'cancelling'
  | 'cancelled'
  | 'completed'
  | 'completedWithErrors'
  | 'failed';

export type SearchableState = 'notApplicable' | 'pending' | 'catchingUp' | 'current';

export interface Progress {
  readonly done: number;
  readonly total: number;
}

export interface SearchableProgress extends Progress {
  readonly state: SearchableState;
}

export interface JobUser {
  readonly userId: string;
  readonly displayName: string;
}

/** `GET …/jobs` item. */
export interface JobSummary {
  readonly jobId: string;
  readonly type: string;
  readonly name: string;
  readonly status: JobState | string;
  readonly createdBy: JobUser;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  /** Saved: committed in the database. */
  readonly committed: Progress;
  /** Searchable: applied to the search index. */
  readonly searchable: SearchableProgress;
  readonly errorCount: number;
  readonly correlationId: string;
  readonly snapshotId: string | null;
  /** Relative app route of the job's own page (e.g. `imports/{id}`), or null. */
  readonly link: string | null;
}

export interface JobChunks {
  readonly pending: number;
  readonly running: number;
  readonly done: number;
  readonly failed: number;
  readonly deadLettered: number;
  readonly cancelled: number;
}

/** `GET …/jobs/{jobId}`. */
export interface JobDetail extends JobSummary {
  readonly chunks: JobChunks;
  readonly attempts: number;
  readonly lastError: string | null;
  readonly etaSeconds: number | null;
}

/** `GET …/jobs/{jobId}/failures` item: a failed or dead-lettered chunk, task or outbox row. */
export interface JobFailure {
  readonly kind: string;
  readonly id: string;
  readonly attempts: number;
  readonly error: string;
  readonly failedAt: string;
}

const FAILURE_KINDS: Record<string, string> = {
  chunk: 'Chunk',
  indexTask: 'Index task',
  outbox: 'Search update',
  deadLetter: 'Dead-lettered message',
};

/** A failure's kind in words; a dead-lettered message is the recorded broker copy (diagnostics only). */
export function failureKindLabel(kind: string): string {
  return FAILURE_KINDS[kind] ?? kind;
}

/** One event of `GET …/job-events` (server-sent events). */
export interface JobEvent {
  readonly jobId: string;
  readonly status: JobState | string;
  readonly committed: Progress;
  readonly searchable: SearchableProgress;
  readonly updatedAt: string;
}

export type CreatedBy = 'me' | 'all';

/** Filters of the Jobs page (query parameters of `GET …/jobs`). */
export interface JobFilter {
  readonly type: string;
  readonly status: string;
  readonly createdBy: CreatedBy;
  /** `yyyy-mm-dd` (local day) or ''. */
  readonly from: string;
  readonly to: string;
}

export interface Page<T> {
  readonly items: readonly T[];
  readonly nextCursor: string | null;
}

/** Job types of the filter, in the words of the sections that start them. */
export const JOB_TYPES: readonly { value: string; label: string }[] = [
  { value: 'import', label: 'Import' },
  { value: 'bulkCoding', label: 'Mass Edit' },
  { value: 'export', label: 'Export' },
  { value: 'production', label: 'Production' },
  { value: 'reindex', label: 'Reindex' },
  { value: 'relationshipFixup', label: 'Family and duplicate updates' },
  { value: 'render', label: 'Rendering' },
];

export function typeLabel(type: string): string {
  return JOB_TYPES.find((t) => t.value === type)?.label ?? humanize(type);
}

const STATES: Record<JobState, { pill: PillStatus; label: string }> = {
  created: { pill: 'queued', label: 'Queued' },
  preparing: { pill: 'queued', label: 'Preparing' },
  running: { pill: 'running', label: 'Running' },
  paused: { pill: 'running', label: 'Paused' },
  cancelling: { pill: 'running', label: 'Cancelling' },
  cancelled: { pill: 'cancelled', label: 'Cancelled' },
  completed: { pill: 'succeeded', label: 'Completed' },
  completedWithErrors: { pill: 'partial', label: 'Completed with errors' },
  failed: { pill: 'failed', label: 'Failed' },
};

/** Status filter options (API values). */
export const STATUS_OPTIONS: readonly { value: string; label: string }[] = (
  ['created', 'running', 'completed', 'completedWithErrors', 'failed', 'cancelled'] as const
).map((value) => ({ value, label: STATES[value].label }));

function known(status: string): status is JobState {
  return Object.hasOwn(STATES, status);
}

/** The status as the shared status pill shows it. */
export function pillStatus(status: string): PillStatus {
  return known(status) ? STATES[status].pill : 'running';
}

export function statusLabel(status: string): string {
  return known(status) ? STATES[status].label : humanize(status);
}

const FINISHED = new Set(['completed', 'completedWithErrors', 'failed', 'cancelled']);

/** The job itself is over (the Saved phase); the Searchable phase may still be catching up. */
export function isFinished(status: string): boolean {
  return FINISHED.has(status);
}

/** Nothing about the job will change any more. */
export function isSettled(job: Pick<JobSummary, 'status' | 'searchable'>): boolean {
  if (!isFinished(job.status)) return false;
  return (
    job.status === 'failed' ||
    job.status === 'cancelled' ||
    job.searchable.state === 'current' ||
    job.searchable.state === 'notApplicable'
  );
}

/** 0–1. A finished job with nothing to save counts as complete. */
export function fraction(p: Progress, complete = false): number {
  if (!p.total) return complete ? 1 : 0;
  return Math.min(1, Math.max(0, p.done / p.total));
}

/** "400 of 1,000". */
export function countText(p: Progress, locale: string): string {
  const n = new Intl.NumberFormat(locale);
  return `${n.format(p.done)} of ${n.format(p.total)}`;
}

export function percentText(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, { style: 'percent' }).format(value);
}

/** "About 3 minutes left" (rounded up), "Less than a minute left", "About 2 hours left". */
export function etaText(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0)
    return null;
  if (seconds < 60) return 'Less than a minute left';
  const minutes = Math.ceil(seconds / 60);
  if (minutes < 90) return `About ${minutes} minute${minutes === 1 ? '' : 's'} left`;
  const hours = Math.round(minutes / 60);
  return `About ${hours} hours left`;
}

/** Plain-language state of the Searchable phase. */
export function searchableText(job: Pick<JobSummary, 'status' | 'searchable'>): string {
  switch (job.searchable.state) {
    case 'notApplicable':
      return 'Not needed for this job';
    case 'current':
      return 'Searchable';
    case 'catchingUp':
      return 'Updating the search index…';
    default:
      return isFinished(job.status) ? 'Waiting for the search index…' : 'After saving';
  }
}

/** Short progress of the tray and the list: "Saved 40% · Searchable 10%". */
export function phasesText(job: JobSummary, locale: string): string {
  const finished = isFinished(job.status);
  const saved = `Saved ${percentText(fraction(job.committed, finished), locale)}`;
  if (job.searchable.state === 'notApplicable') return saved;
  const searchable =
    job.searchable.state === 'current'
      ? 'Searchable'
      : `Searchable ${percentText(fraction(job.searchable), locale)}`;
  return `${saved} · ${searchable}`;
}

/** Permission names (docs/security/permission-matrix.md); the contract's finer names are accepted too. */
export const JOB_PERMISSIONS = {
  viewAll: 'Job.ViewAll',
  /** Retrying failed work is `Job.Replay` alone (ADR-015 D5.7); cancelling is the owner or `Job.Manage`. */
  replay: ['Job.Replay'],
  cancel: ['Job.Manage'],
} as const;

export interface Caller {
  readonly userId: string | null;
  can(permission: string): boolean;
}

export function canViewAll(caller: Pick<Caller, 'can'>): boolean {
  return caller.can(JOB_PERMISSIONS.viewAll);
}

export function canRetry(caller: Pick<Caller, 'can'>): boolean {
  return JOB_PERMISSIONS.replay.some((p) => caller.can(p));
}

/** The owner may cancel their own running job; others need the cancel permission. */
export function canCancel(job: JobSummary, caller: Caller): boolean {
  if (isFinished(job.status) || job.status === 'cancelling') return false;
  return (
    (!!caller.userId && job.createdBy.userId === caller.userId) ||
    JOB_PERMISSIONS.cancel.some((p) => caller.can(p))
  );
}

export function failedChunks(job: JobDetail): number {
  return job.chunks.failed + job.chunks.deadLettered;
}

/** A live update applied to a job; null when the update is older than what is shown. */
export function applyEvent<T extends JobSummary>(job: T, event: JobEvent): T | null {
  if (Date.parse(event.updatedAt) < Date.parse(job.updatedAt)) return null;
  return {
    ...job,
    status: event.status,
    committed: event.committed,
    searchable: event.searchable,
    updatedAt: event.updatedAt,
  };
}

/** The newer of two copies of a job (by `updatedAt`); detail-only fields of `a` (chunks, attempts…) are kept. */
export function newer<T extends JobSummary>(a: T, b: JobSummary | undefined): T {
  if (!b || Date.parse(b.updatedAt) <= Date.parse(a.updatedAt)) return a;
  return { ...a, ...b };
}

/** Does a job belong in the list for these filters? (New jobs arriving live are added only when they match.) */
export function matchesFilter(job: JobSummary, filter: JobFilter, userId: string | null): boolean {
  if (filter.type && job.type !== filter.type) return false;
  if (filter.status && job.status !== filter.status) return false;
  if (filter.createdBy === 'me' && job.createdBy.userId !== userId) return false;
  const created = Date.parse(job.createdAt);
  const from = dayStart(filter.from);
  const to = dayEnd(filter.to);
  if (from && created < Date.parse(from)) return false;
  if (to && created > Date.parse(to)) return false;
  return true;
}

/** Start of a local `yyyy-mm-dd` day as ISO-8601, or null. */
export function dayStart(day: string): string | null {
  const d = parseDay(day);
  return d ? d.toISOString() : null;
}

/** End of a local `yyyy-mm-dd` day as ISO-8601, or null. */
export function dayEnd(day: string): string | null {
  const d = parseDay(day);
  if (!d) return null;
  d.setDate(d.getDate() + 1);
  return new Date(d.getTime() - 1).toISOString();
}

function parseDay(day: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!m) return null;
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Query parameters of `GET …/jobs` for the filters. */
export function filterParams(filter: JobFilter): Record<string, string> {
  const params: Record<string, string> = { createdBy: filter.createdBy };
  if (filter.type) params['type'] = filter.type;
  if (filter.status) params['status'] = filter.status;
  const from = dayStart(filter.from);
  const to = dayEnd(filter.to);
  if (from) params['from'] = from;
  if (to) params['to'] = to;
  return params;
}

/** The notification for a job that just finished: message and tone (failures persist until dismissed). */
export function completionNotice(job: JobSummary): { message: string; tone: ToastTone } | null {
  const what = `${typeLabel(job.type)} “${job.name}”`;
  switch (job.status) {
    case 'completed':
      return {
        message:
          job.searchable.state === 'current' || job.searchable.state === 'notApplicable'
            ? `${what} completed.`
            : `${what} completed. Saved; becoming searchable.`,
        tone: 'success',
      };
    case 'completedWithErrors':
      return {
        message: `${what} completed with ${job.errorCount} error${job.errorCount === 1 ? '' : 's'}.`,
        tone: 'warning',
      };
    case 'failed':
      return { message: `${what} failed.`, tone: 'error' };
    case 'cancelled':
      return { message: `${what} was cancelled.`, tone: 'info' };
    default:
      return null;
  }
}

function humanize(value: string): string {
  const spaced = value.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

// Tolerant readers: numbers may arrive as strings (int64), optional fields may be missing, and the M1 `JobResource`
// shape of `GET …/jobs/{id}` (jobType, initiatedBy, committed.chunks*, indexed) is read until #61 replaces it.

type Raw = Record<string, unknown>;

const num = (v: unknown): number => {
  const n = Number(v ?? 0);
  return Number.isFinite(n) ? n : 0;
};
const str = (v: unknown, fallback = ''): string =>
  v === null || v === undefined ? fallback : String(v);
const strOrNull = (v: unknown): string | null =>
  v === null || v === undefined || v === '' ? null : String(v);
const obj = (v: unknown): Raw => (v && typeof v === 'object' ? (v as Raw) : {});

const SEARCHABLE_STATES = new Set<string>(['notApplicable', 'pending', 'catchingUp', 'current']);

function toSearchable(raw: Raw, legacyIndexed: Raw | null): SearchableProgress {
  if (legacyIndexed) {
    const state = str(legacyIndexed['state']);
    return {
      done: num(legacyIndexed['indexTasksApplied']),
      total: num(legacyIndexed['indexTasksTotal']),
      state: state === 'current' ? 'current' : state === 'indexing' ? 'catchingUp' : 'pending',
    };
  }
  const state = str(raw['state'], 'pending');
  return {
    done: num(raw['done']),
    total: num(raw['total']),
    state: (SEARCHABLE_STATES.has(state) ? state : 'pending') as SearchableState,
  };
}

function toCommitted(raw: Raw): Progress {
  if ('chunksTotal' in raw) {
    return {
      done: num(raw['chunksCommitted']) + num(raw['chunksFailed']) + num(raw['chunksCancelled']),
      total: num(raw['chunksTotal']),
    };
  }
  return { done: num(raw['done']), total: num(raw['total']) };
}

export function toJobSummary(body: unknown): JobSummary {
  const raw = obj(body);
  const legacy = 'jobType' in raw || 'indexed' in raw;
  const type = str(raw['type'] ?? raw['jobType'], 'job');
  const createdBy = obj(raw['createdBy']);
  const initiatedBy = strOrNull(raw['initiatedBy']);
  return {
    jobId: str(raw['jobId']),
    type,
    name: str(raw['name'], typeLabel(type)),
    status: str(raw['status'], 'created'),
    createdBy: {
      userId: str(createdBy['userId'] ?? initiatedBy),
      displayName: str(createdBy['displayName'] ?? createdBy['userId'] ?? initiatedBy),
    },
    createdAt: str(raw['createdAt']),
    updatedAt: str(raw['updatedAt'] ?? raw['finishedAt'] ?? raw['startedAt'] ?? raw['createdAt']),
    completedAt: strOrNull(raw['completedAt'] ?? raw['finishedAt']),
    committed: toCommitted(obj(raw['committed'])),
    searchable: toSearchable(
      obj(raw['searchable']),
      legacy && !('searchable' in raw) ? obj(raw['indexed']) : null,
    ),
    errorCount: num(raw['errorCount'] ?? obj(raw['committed'])['chunksFailed']),
    correlationId: str(raw['correlationId']),
    snapshotId: strOrNull(raw['snapshotId'] ?? raw['targetSnapshotId']),
    link: strOrNull(raw['link']),
  };
}

export function toJobDetail(body: unknown): JobDetail {
  const raw = obj(body);
  const summary = toJobSummary(raw);
  const chunks = obj(raw['chunks']);
  const committed = obj(raw['committed']);
  const legacyChunks = 'chunksTotal' in committed;
  return {
    ...summary,
    chunks: legacyChunks
      ? {
          pending: num(committed['chunksPending']),
          running: 0,
          done: num(committed['chunksCommitted']),
          failed: num(committed['chunksFailed']),
          deadLettered: 0,
          cancelled: num(committed['chunksCancelled']),
        }
      : {
          pending: num(chunks['pending']),
          running: num(chunks['running']),
          done: num(chunks['done']),
          failed: num(chunks['failed']),
          deadLettered: num(chunks['deadLettered']),
          cancelled: num(chunks['cancelled']),
        },
    attempts: num(raw['attempts']),
    lastError: strOrNull(raw['lastError'] ?? raw['statusReason']),
    etaSeconds:
      raw['etaSeconds'] === null || raw['etaSeconds'] === undefined ? null : num(raw['etaSeconds']),
  };
}

export function toJobEvent(body: unknown): JobEvent | null {
  const raw = obj(body);
  const jobId = strOrNull(raw['jobId']);
  if (!jobId) return null;
  return {
    jobId,
    status: str(raw['status'], 'running'),
    committed: toCommitted(obj(raw['committed'])),
    searchable: toSearchable(obj(raw['searchable']), null),
    updatedAt: str(raw['updatedAt'], new Date().toISOString()),
  };
}

export function toJobFailure(body: unknown): JobFailure {
  const raw = obj(body);
  return {
    kind: str(raw['kind'], 'chunk'),
    id: str(raw['id']),
    attempts: num(raw['attempts']),
    error: str(raw['error']),
    failedAt: str(raw['failedAt']),
  };
}
