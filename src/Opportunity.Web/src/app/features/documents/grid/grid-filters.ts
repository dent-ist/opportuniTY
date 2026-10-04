import type { FieldResource } from '../../../core/api/generated/models';
import type { ColumnFormat } from './grid-columns';

/** The control a column's filter uses, from the field type (`GET …/fields`). */
export type FilterKind = 'text' | 'date' | 'number' | 'size' | 'choice' | 'boolean';

/** A filterable column of the filter row. */
export interface FilterSpec {
  readonly queryName: string;
  readonly label: string;
  readonly kind: FilterKind;
  /** Text: a plain value means "contains" (the field takes leading wildcards) instead of "starts with". */
  readonly contains: boolean;
  /** Choice names (all of them: documents can still carry an inactive choice). */
  readonly choices: readonly string[];
}

/** One column's filter condition. */
export type FilterValue =
  | { readonly op: 'text'; readonly text: string }
  | { readonly op: 'range'; readonly from: string; readonly to: string }
  | { readonly op: 'choices'; readonly names: readonly string[] }
  | { readonly op: 'boolean'; readonly value: boolean }
  | { readonly op: 'has' }
  | { readonly op: 'empty' };

/** Active filters by query name, in the order they were added. */
export type FilterSet = ReadonlyMap<string, FilterValue>;

/** The filter for a column, or null when the field is unknown or lacks the `filterable` capability. */
export function filterSpec(
  field: FieldResource | undefined,
  label: string,
  format: ColumnFormat,
): FilterSpec | null {
  if (!field?.capabilities?.filterable) return null;
  const base = { queryName: field.queryName, label, contains: false, choices: [] as string[] };
  switch (field.type) {
    case 'date':
      return { ...base, kind: 'date' };
    case 'integer':
    case 'decimal':
      return { ...base, kind: format === 'size' ? 'size' : 'number' };
    case 'boolean':
      return { ...base, kind: 'boolean' };
    case 'singleChoice':
    case 'multiChoice':
      return { ...base, kind: 'choice', choices: (field.choices ?? []).map((c) => c.name) };
    default:
      return { ...base, kind: 'text', contains: field.capabilities.leadingWildcard };
  }
}

// ── Compilation into the query language (ADR-008) ───────────────────────────────────────────────────────────

/** Characters a term must escape (R4 reserved set, whitespace, wildcards, `/` so no token reads as `W/n`). */
const TERM_ESCAPE = /[()":[\]{}\\!~^*?/\s]/g;
const OPERATORS = new Set(['AND', 'OR', 'NOT', 'TO']);

/** `value` as one literal term: every special character escaped (R4), never an operator (R1). */
export function escapeTerm(value: string): string {
  let term = value.replace(TERM_ESCAPE, (c) => `\\${c}`);
  if (/^[-+]/.test(term) || OPERATORS.has(term)) term = `\\${term}`;
  return term;
}

/** `value` as a phrase: only `"` and `\` are escaped inside quotes (R4). */
export function quotePhrase(value: string): string {
  return `"${value.replace(/["\\]/g, (c) => `\\${c}`)}"`;
}

/** Sizes in bytes, or with a binary unit ("10 KB", "2.5 MB"). */
const SIZE = /^(\d+(?:\.\d+)?)\s*(b|kb|mb|gb|tb)?$/i;
const UNITS: Record<string, number> = {
  b: 1,
  kb: 1024,
  mb: 1024 ** 2,
  gb: 1024 ** 3,
  tb: 1024 ** 4,
};
const NUMBER = /^-?\d+(\.\d+)?$/;
/** ADR-008 R8 date literals: a day, a month or a year. */
const DATE = /^\d{4}(-\d{2}(-\d{2})?)?$/;

export function parseSize(text: string): number | null {
  const m = SIZE.exec(text.trim());
  return m ? Math.round(Number(m[1]) * UNITS[(m[2] ?? 'b').toLowerCase()]) : null;
}

/** A range bound in invariant notation (R9), or null when it is not valid for the kind. */
function boundValue(kind: FilterKind, text: string): number | string | null {
  const t = text.trim();
  if (kind === 'size') return parseSize(t);
  if (kind === 'number') return NUMBER.test(t) ? Number(t) : null;
  return DATE.test(t) && !Number.isNaN(Date.parse(t.length === 4 ? `${t}-01-01` : t)) ? t : null;
}

/** Why a value cannot be applied (shown in the filter), or null. Empty values are valid: they clear. */
export function filterProblem(spec: FilterSpec, value: FilterValue): string | null {
  if (value.op !== 'range') return null;
  const parts = [value.from, value.to].map((t) => (t.trim() ? boundValue(spec.kind, t) : '*'));
  if (parts.some((p) => p === null)) {
    return spec.kind === 'date'
      ? 'Enter dates as YYYY-MM-DD.'
      : spec.kind === 'size'
        ? 'Enter sizes in bytes or with a unit, e.g. 10 KB or 2.5 MB.'
        : 'Enter numbers with digits and an optional "." for decimals.';
  }
  const [from, to] = parts;
  if (
    from !== '*' &&
    to !== '*' &&
    (spec.kind === 'date' ? from! > to! : Number(from) > Number(to))
  ) {
    return '"From" is after "To".';
  }
  return null;
}

/**
 * The query-language clause of one filter (ADR-008): text "starts with" (`field:value*`, R7 trailing wildcard) or
 * "contains" (`field:*value*` where the field takes leading wildcards), `"quoted"` text as an exact phrase, ranges
 * `field:[from TO to]` with `*` for an open end (R9), choices ORed in a group, booleans, and presence
 * (`field:*` / `NOT field:*`). Null when the value is empty or not valid.
 */
export function compileFilter(spec: FilterSpec, value: FilterValue): string | null {
  const f = spec.queryName;
  switch (value.op) {
    case 'has':
      return `${f}:*`;
    case 'empty':
      return `NOT ${f}:*`;
    case 'boolean':
      return `${f}:${value.value ? 'true' : 'false'}`;
    case 'choices':
      if (value.names.length === 0) return null;
      return value.names.length === 1
        ? `${f}:${quotePhrase(value.names[0])}`
        : `${f}:(${value.names.map(quotePhrase).join(' OR ')})`;
    case 'text': {
      const text = value.text.trim();
      if (!text) return null;
      const exact = /^"([\s\S]*)"$/.exec(text);
      if (exact && exact[1].trim()) return `${f}:${quotePhrase(exact[1])}`;
      const term = escapeTerm(text);
      return spec.contains ? `${f}:*${term}*` : `${f}:${term}*`;
    }
    case 'range': {
      if (filterProblem(spec, value)) return null;
      const [from, to] = [value.from, value.to].map((t) =>
        t.trim() ? escapeTerm(String(boundValue(spec.kind, t))) : '*',
      );
      return from === '*' && to === '*' ? null : `${f}:[${from} TO ${to}]`;
    }
  }
}

export interface ClauseSpan {
  readonly queryName: string;
  readonly start: number;
  readonly end: number;
}

/** The query sent to the search: the keyword query ANDed with every filter clause. */
export interface CompiledQuery {
  readonly query: string;
  /** Where each filter's clause is in `query` (for positioned query errors). */
  readonly clauses: readonly ClauseSpan[];
}

/**
 * `(keyword) AND clause AND clause …`. Without filters the keyword query is sent unchanged; the keyword part is
 * grouped so its own ORs stay inside it.
 */
export function compileQuery(
  keyword: string,
  clauses: readonly { queryName: string; clause: string }[],
): CompiledQuery {
  if (clauses.length === 0) return { query: keyword, clauses: [] };
  let query = keyword.trim() ? `(${keyword})` : '';
  const spans: ClauseSpan[] = [];
  for (const { queryName, clause } of clauses) {
    if (query) query += ' AND ';
    spans.push({ queryName, start: query.length, end: query.length + clause.length });
    query += clause;
  }
  return { query, clauses: spans };
}

/** The filter whose clause holds a query error at `start`, if any. */
export function clauseAt(compiled: CompiledQuery, start: number): string | null {
  return compiled.clauses.find((c) => start >= c.start && start < c.end)?.queryName ?? null;
}

// ── Labels ──────────────────────────────────────────────────────────────────────────────────────────────────

/** Short description of a filter for its trigger, its chip and screen readers ("starts with “RE”"). */
export function describeFilter(spec: FilterSpec, value: FilterValue): string {
  switch (value.op) {
    case 'has':
      return 'has a value';
    case 'empty':
      return 'is empty';
    case 'boolean':
      return value.value ? 'Yes' : 'No';
    case 'choices':
      return value.names.join(' or ');
    case 'text': {
      const text = value.text.trim();
      if (/^"[\s\S]+"$/.test(text)) return `is ${text}`;
      return `${spec.contains ? 'contains' : 'starts with'} “${text}”`;
    }
    case 'range': {
      const from = value.from.trim();
      const to = value.to.trim();
      if (from && to) return `${from} to ${to}`;
      return from ? `from ${from}` : `up to ${to}`;
    }
  }
}
