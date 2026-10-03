import { SEARCH_FIELD_TYPE_LABELS, SearchField } from '../../../core/search/search-fields';
import { QueryToken, isFieldName, tokenize } from './query-lexer';

/** What the caret is on: a field name being typed, or the value after `field:`. */
export type CompletionContext =
  | { kind: 'field'; prefix: string; from: number; to: number }
  | { kind: 'value'; field: string; prefix: string; from: number; to: number };

export interface Suggestion {
  /** Replacement text for `from`–`to`. */
  readonly insert: string;
  readonly label: string;
  readonly detail: string;
  readonly from: number;
  readonly to: number;
  /** The field whose values to offer right after inserting this field name. */
  readonly thenValuesOf?: SearchField;
}

export const MAX_SUGGESTIONS = 12;

/**
 * Completion context at `caret`. `explicit` (Ctrl+Space) also offers fields where nothing is typed yet.
 * Returns null where nothing fits: inside ranges, operators, plain phrases.
 */
export function completionContext(
  text: string,
  caret: number,
  explicit = false,
  tokens: readonly QueryToken[] = tokenize(text),
): CompletionContext | null {
  const index = tokens.findIndex((t) => t.start < caret && caret <= t.end);
  const token = tokens[index];
  const previous = index > 0 ? tokens[index - 1] : undefined;
  if (!token) {
    return explicit ? { kind: 'field', prefix: '', from: caret, to: caret } : null;
  }
  if (token.kind === 'field') {
    if (caret === token.end) {
      const next = tokens[index + 1];
      const to = next && next.start === token.end && isValueToken(next) ? next.end : caret;
      return { kind: 'value', field: token.value, prefix: '', from: caret, to };
    }
    return {
      kind: 'field',
      prefix: text.slice(token.start, caret),
      from: token.start,
      to: token.end,
    };
  }
  if (previous?.kind === 'field' && previous.end === token.start && isValueToken(token)) {
    let prefix = text.slice(token.start, caret);
    if (token.kind === 'phrase') prefix = prefix.slice(1).replace(/\\(.)/g, '$1');
    return { kind: 'value', field: previous.value, prefix, from: token.start, to: token.end };
  }
  if (token.kind === 'term' && /^\p{L}/u.test(token.value)) {
    const prefix = text.slice(token.start, caret);
    if (isFieldName(prefix)) return { kind: 'field', prefix, from: token.start, to: token.end };
  }
  return null;
}

function isValueToken(token: QueryToken): boolean {
  return (
    token.kind === 'term' ||
    token.kind === 'wildcard' ||
    token.kind === 'phrase' ||
    token.kind === 'exists' ||
    token.kind === 'operator'
  );
}

export function suggest(
  context: CompletionContext,
  fields: readonly SearchField[],
): readonly Suggestion[] {
  const { from, to } = context;
  if (context.kind === 'field') {
    const prefix = context.prefix.toLowerCase();
    const ranked = fields
      .map((field) => ({ field, rank: fieldRank(field, prefix) }))
      .filter((m) => m.rank >= 0)
      .sort((a, b) => a.rank - b.rank || a.field.queryName.localeCompare(b.field.queryName));
    return ranked.slice(0, MAX_SUGGESTIONS).map(({ field }): Suggestion => ({
      insert: `${field.queryName}:`,
      label: field.queryName,
      detail: `${field.displayName} · ${SEARCH_FIELD_TYPE_LABELS[field.type]}`,
      from,
      to,
      thenValuesOf: hasValueList(field) ? field : undefined,
    }));
  }
  const field = fields.find((f) => f.queryName.toLowerCase() === context.field.toLowerCase());
  if (!field) return [];
  const prefix = context.prefix.toLowerCase();
  const values: Suggestion[] = valueList(field)
    .filter((v) => v.toLowerCase().includes(prefix))
    .sort(
      (a, b) =>
        Number(!a.toLowerCase().startsWith(prefix)) - Number(!b.toLowerCase().startsWith(prefix)),
    )
    .map((v) => ({
      insert: quoteValue(v),
      label: v,
      detail: field.type === 'boolean' ? 'Yes/No' : 'Choice',
      from,
      to,
    }));
  if (!prefix || '*'.startsWith(prefix)) {
    values.push({ insert: '*', label: '*', detail: 'Has any value (is set)', from, to });
  }
  return values.slice(0, MAX_SUGGESTIONS);
}

/** 0: query name starts with the prefix; 1: a display-name word does; 2: contains; -1: no match. */
function fieldRank(field: SearchField, prefix: string): number {
  const name = field.queryName.toLowerCase();
  if (name.startsWith(prefix)) return 0;
  const display = field.displayName.toLowerCase();
  if (display.split(/[\s()/-]+/).some((w) => w.startsWith(prefix))) return 1;
  return prefix.length >= 2 && (name.includes(prefix) || display.includes(prefix)) ? 2 : -1;
}

function hasValueList(field: SearchField): boolean {
  return valueList(field).length > 0;
}

function valueList(field: SearchField): readonly string[] {
  if (field.type === 'boolean') return ['yes', 'no'];
  if (field.type === 'singleChoice' || field.type === 'multiChoice') return field.choices ?? [];
  return [];
}

const NEEDS_QUOTES = /[\s()"[\]{}:\\!~^*?]|^[-+]|^(AND|OR|NOT|TO)$|^[Ww]\/[0-9]+$/;

/** A value as query text: a phrase (with `"`, `\`, `*` and `?` escaped) when it would not be one literal term. */
export function quoteValue(value: string): string {
  return NEEDS_QUOTES.test(value) ? `"${value.replace(/["\\*?]/g, '\\$&')}"` : value;
}
