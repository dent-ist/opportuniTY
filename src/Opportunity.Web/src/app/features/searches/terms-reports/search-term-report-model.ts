import { ApiError, describeError } from '../../../core/api/problem-details';
import type { JobStatus as PillStatus } from '../../../ui';
import type {
  ReportScope,
  ReportScopeKind,
  ReportTerm,
  SearchTermReportSummary,
  TermDraft,
  TermError,
} from './search-term-report-api';

// Pure rules of the Search Terms Reports screens (#180, familiarity guide §2.3 and §5 step 5): reading pasted and
// uploaded terms, wording, sorting and who may do what. No Angular here, so every rule is unit-tested.

/** Documents route parameters that open one term's hits (`?termReport=<reportId>&term=<termId>`). */
export const TERM_REPORT_PARAM = 'termReport';
export const TERM_PARAM = 'term';

/** New-report route parameters that pre-fill the scope (the Saved Searches "Add to new Search Terms Report"). */
export const SCOPE_SAVED_SEARCH_PARAM = 'savedSearch';
export const SCOPE_SNAPSHOT_PARAM = 'frozenSet';

// ── Reading terms ───────────────────────────────────────────────────────────────────────────────────────────

/** A line of the input that was not used, and why. */
export interface TermIssue {
  /** 1-based line of the pasted text or the CSV file. */
  readonly line: number;
  readonly message: string;
}

export interface ParsedTerms {
  readonly terms: readonly TermDraft[];
  /** Lines that were skipped; the other terms are still used. */
  readonly issues: readonly TermIssue[];
  /** Problems that must be fixed before the report can run (e.g. one name for two expressions). */
  readonly blocking: readonly string[];
}

/**
 * Pasted terms: one per line, the expression doubling as the name. A line copied from a spreadsheet with two columns
 * (name, then expression, separated by a tab) keeps its name.
 */
export function parsePastedTerms(text: string): ParsedTerms {
  const rows: { line: number; name: string; expression: string }[] = [];
  text.split(/\r\n|\n|\r/).forEach((raw, i) => {
    const line = raw.trim();
    if (!line) return;
    const tab = raw.indexOf('\t');
    if (tab >= 0) {
      const name = raw.slice(0, tab).trim();
      const expression = raw.slice(tab + 1).trim();
      rows.push({ line: i + 1, name: name || expression, expression });
    } else {
      rows.push({ line: i + 1, name: line, expression: line });
    }
  });
  return finish(rows, []);
}

/**
 * A CSV file of `Name,Expression` rows (RFC 4180 quoting; a header row `Name,Expression` is optional; a row with one
 * column is an expression that is its own name). An expression that contains commas must be quoted.
 */
export function parseTermsCsv(text: string): ParsedTerms {
  const records = readCsv(text.replace(/^﻿/, ''));
  const issues: TermIssue[] = [];
  const rows: { line: number; name: string; expression: string }[] = [];
  records.forEach(({ line, cells }, i) => {
    const values = cells.map((c) => c.trim());
    if (values.every((v) => !v)) return;
    if (
      i === 0 &&
      values[0]?.toLowerCase() === 'name' &&
      (values[1] ?? '').toLowerCase() === 'expression'
    )
      return;
    if (values.length > 2 && values.slice(2).some((v) => v)) {
      issues.push({
        line,
        message: 'More than two columns. Put the expression in double quotes when it has commas.',
      });
      return;
    }
    const [name, expression = ''] = values;
    if (values.length === 1) rows.push({ line, name, expression: name });
    else if (!expression) issues.push({ line, message: 'No expression.' });
    else rows.push({ line, name: name || expression, expression });
  });
  return finish(rows, issues);
}

interface CsvRecord {
  readonly line: number;
  readonly cells: string[];
}

/** RFC 4180 records with the line each starts on (quoted cells may span lines). */
function readCsv(text: string): CsvRecord[] {
  const records: CsvRecord[] = [];
  let cells: string[] = [];
  let cell = '';
  let quoted = false;
  let line = 1;
  let start = 1;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (c === '"') quoted = false;
      else {
        if (c === '\n') line++;
        cell += c;
      }
    } else if (c === '"' && cell.trim() === '') {
      cell = '';
      quoted = true;
    } else if (c === ',') {
      cells.push(cell);
      cell = '';
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      cells.push(cell);
      records.push({ line: start, cells });
      cells = [];
      cell = '';
      line++;
      start = line;
    } else cell += c;
  }
  if (cell !== '' || cells.length) {
    cells.push(cell);
    records.push({ line: start, cells });
  }
  return records;
}

function finish(
  rows: readonly { line: number; name: string; expression: string }[],
  issues: TermIssue[],
): ParsedTerms {
  const seen = new Map<string, { expression: string; line: number }>();
  const terms: TermDraft[] = [];
  const blocking: string[] = [];
  for (const row of rows) {
    const key = row.name.toLocaleLowerCase();
    const earlier = seen.get(key);
    if (earlier && earlier.expression === row.expression) {
      issues.push({ line: row.line, message: `Same term as line ${earlier.line}; skipped.` });
      continue;
    }
    if (earlier) {
      blocking.push(
        `“${row.name}” names two different expressions (lines ${earlier.line} and ${row.line}). Give each term its own name.`,
      );
      continue;
    }
    seen.set(key, { expression: row.expression, line: row.line });
    terms.push({ name: row.name, expression: row.expression });
  }
  return { terms, issues: [...issues].sort((a, b) => a.line - b.line), blocking };
}

// ── Wording ─────────────────────────────────────────────────────────────────────────────────────────────────

export const SCOPE_KINDS: readonly { value: ReportScopeKind; label: string; hint: string }[] = [
  {
    value: 'savedSearch',
    label: 'Saved search',
    hint: 'The documents the saved search finds when the report runs.',
  },
  {
    value: 'snapshot',
    label: 'Frozen set',
    hint: 'The documents of an existing frozen set, unchanged since it was taken.',
  },
  { value: 'workspace', label: 'Whole workspace', hint: 'Every document in the workspace.' },
];

/** "Whole workspace", "Saved search “Hot docs”", "Frozen set “Mass Edit 2026-10-03”". */
export function scopeLabel(scope: ReportScope): string {
  switch (scope.kind) {
    case 'savedSearch':
      return `Saved search “${scope.name ?? scope.id ?? 'unknown'}”`;
    case 'snapshot':
      return `Frozen set “${scope.name ?? scope.id ?? 'unknown'}”`;
    default:
      return 'Whole workspace';
  }
}

const STATUS: Record<string, { pill: PillStatus; label: string }> = {
  queued: { pill: 'queued', label: 'Queued' },
  running: { pill: 'running', label: 'Running' },
  completed: { pill: 'succeeded', label: 'Completed' },
  failed: { pill: 'failed', label: 'Failed' },
};

export function statusPill(status: string): PillStatus {
  return STATUS[status]?.pill ?? 'running';
}

export function isReportFinished(status: string): boolean {
  return status === 'completed' || status === 'failed';
}

/** "Syntax error at character 9: Missing closing quote." (positions count from 1 for people). */
export function termErrorText(error: TermError): string {
  const message = error.message.trim().replace(/\.?$/, '.');
  return error.position === null
    ? `Syntax error: ${message}`
    : `Syntax error at character ${error.position + 1}: ${message}`;
}

/** The expression split around the error position, so the character can be marked. */
export function errorParts(
  expression: string,
  position: number | null,
): { before: string; at: string; after: string } | null {
  if (position === null || position < 0) return null;
  const p = Math.min(position, expression.length);
  // A position at the end (e.g. a missing closing quote) marks an empty spot after the text.
  return {
    before: expression.slice(0, p),
    at: expression.slice(p, p + 1),
    after: expression.slice(p + 1),
  };
}

/**
 * Whose view of the documents the counts are: the person who ran the report. Restricted or walled documents they could
 * not see are not counted, and the screen says so plainly (Q-11, Q-13).
 */
export function visibilityNotice(
  createdBy: { userId: string; displayName: string },
  me: string | null,
): string {
  return createdBy.userId === me
    ? 'Counts include only the documents you could see when you ran this report. Documents you may not see are not counted.'
    : `Counts include only the documents ${createdBy.displayName} could see when they ran this report. Documents they may not see are not counted, and you may see fewer documents when you open a term.`;
}

export const INDEX_NOT_CURRENT =
  'The search index was still catching up with recent changes when this report ran, so documents changed just before it may not be counted correctly. Re-running uses the same frozen set and gives the same numbers; to count the latest changes, create a new report.';

// ── Sorting ─────────────────────────────────────────────────────────────────────────────────────────────────

export type TermSortKey =
  | 'entered'
  | 'name'
  | 'documentsWithHits'
  | 'documentsWithHitsIncludingFamily'
  | 'uniqueHits'
  | 'uniqueHitsIncludingFamily';

export interface TermSort {
  readonly key: TermSortKey;
  readonly direction: 'asc' | 'desc';
}

/** The order the terms were entered in (ascending), or a column; terms with an error always come last. */
export function sortTerms(terms: readonly ReportTerm[], sort: TermSort): ReportTerm[] {
  const order = new Map(terms.map((t, i) => [t.termId, i]));
  const sign = sort.direction === 'asc' ? 1 : -1;
  const compare = (a: ReportTerm, b: ReportTerm): number => {
    if (!!a.error !== !!b.error) return a.error ? 1 : -1;
    let c = 0;
    if (sort.key === 'name')
      c = a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true });
    else if (sort.key !== 'entered') c = (a[sort.key] ?? -1) - (b[sort.key] ?? -1);
    else c = order.get(a.termId)! - order.get(b.termId)!;
    return c * sign || order.get(a.termId)! - order.get(b.termId)!;
  };
  return [...terms].sort(compare);
}

/** Next sort after activating a column header: numbers start high to low, names A to Z; a third press restores the entered order. */
export function nextSort(current: TermSort, key: TermSortKey): TermSort {
  const first: TermSort['direction'] = key === 'name' ? 'asc' : 'desc';
  if (current.key !== key) return { key, direction: first };
  if (current.direction === first) return { key, direction: first === 'asc' ? 'desc' : 'asc' };
  return { key: 'entered', direction: 'asc' };
}

// ── Permissions ─────────────────────────────────────────────────────────────────────────────────────────────

export interface Caller {
  readonly userId: string | null;
  readonly can: (permission: string) => boolean;
}

/** The person who ran it, or a workspace admin (Workspace.ManageUsers, #72), may rerun or delete a report; the API decides every call. */
export function canDelete(
  report: Pick<SearchTermReportSummary, 'createdBy'>,
  caller: Caller,
): boolean {
  return report.createdBy.userId === caller.userId || caller.can('Workspace.ManageUsers');
}

/** Raw search generations are for admins and support only (Q-73). */
export function canSeeGenerations(caller: Pick<Caller, 'can'>): boolean {
  return caller.can('Job.ViewAll') || caller.can('Workspace.ManageUsers');
}

/** "Search terms – Oct 5, 2026": the default name of a new report. */
export function defaultReportName(now: Date, locale: string): string {
  return `Search terms – ${new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(now)}`;
}

/** A failed call in plain words; 404 does not say whether the report exists (ADR-019 §2.3). */
export function reportErrorText(error: ApiError): string {
  if (error.status === 404)
    return 'This Search Terms Report is not available. It may have been deleted.';
  if (error.status === 403) return 'You do not have permission to do that.';
  if (error.status === 400 || error.status === 422) {
    const first = Object.values(error.problem.errors ?? {})[0]?.[0];
    return first ?? error.problem.detail ?? 'Some values need attention.';
  }
  return describeError(error).detail;
}
