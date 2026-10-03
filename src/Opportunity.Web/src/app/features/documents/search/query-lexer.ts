/**
 * Client-side tokenizer for the query language (ADR-008 §1), used only for highlighting and autocomplete.
 * It follows the server lexer (Opportunity.Core QueryLexer) but never fails: text the server would reject is
 * returned as an `invalid` token so the bar can still highlight the rest. The validate endpoint stays the
 * single authority on whether a query is valid. Offsets are UTF-16 code units, like the server's spans.
 */
export type QueryTokenKind =
  | 'term'
  | 'wildcard'
  | 'phrase'
  | 'field'
  | 'exists'
  | 'operator'
  | 'proximity'
  | 'paren'
  | 'range-bracket'
  | 'range-to'
  | 'range-bound'
  | 'invalid';

export interface QueryToken {
  readonly kind: QueryTokenKind;
  readonly start: number;
  readonly end: number;
  /** Field name for `field` tokens (without the colon); the raw text otherwise. */
  readonly value: string;
  /** A phrase without its closing quote. */
  readonly unterminated?: boolean;
}

const RESERVED = new Set(['(', ')', '"', ':', '[', ']', '{', '}', '\\', '!', '~', '^']);
const OPERATORS = new Set(['AND', 'OR', 'NOT', 'TO']);
const FIELD_NAME = /^\p{L}[\p{L}\p{Nd}_.-]*$/u;
const PROXIMITY = /^[Ww]\/[0-9]+$/;
const UNSUPPORTED = /^(PRE\/[0-9]+|W\/[sp])$/i;
const WHITESPACE = /\s/;

export function isFieldName(name: string): boolean {
  return FIELD_NAME.test(name);
}

export function tokenize(text: string): QueryToken[] {
  const tokens: QueryToken[] = [];
  /** Open range bracket waiting for its close (ranges do not nest). */
  let inRange = false;
  let i = 0;
  const push = (kind: QueryTokenKind, start: number, end: number, value = text.slice(start, end)) =>
    tokens.push({ kind, start, end, value });

  while (i < text.length) {
    const c = text[i];
    if (WHITESPACE.test(c)) {
      i++;
      continue;
    }
    switch (c) {
      case '(':
      case ')':
        push('paren', i, ++i);
        continue;
      case '[':
      case '{':
        inRange = true;
        push('range-bracket', i, ++i);
        continue;
      case ']':
      case '}':
        inRange = false;
        push('range-bracket', i, ++i);
        continue;
      case '"': {
        const start = i;
        i++;
        while (i < text.length && text[i] !== '"') i += text[i] === '\\' ? 2 : 1;
        const closed = i < text.length;
        i = Math.min(text.length, closed ? i + 1 : i);
        tokens.push({
          kind: inRange ? 'range-bound' : 'phrase',
          start,
          end: i,
          value: text.slice(start, i),
          ...(closed ? {} : { unterminated: true }),
        });
        continue;
      }
      case ':':
      case '!':
      case '~':
      case '^':
      case '-':
      case '+':
        // Reserved at the start of a token: the server reports the exact reason.
        push('invalid', i, ++i);
        continue;
    }

    const start = i;
    let escaped = false;
    let wildcard = false;
    while (i < text.length) {
      const ch = text[i];
      if (ch === '\\') {
        escaped = true;
        i = Math.min(text.length, i + 2);
        continue;
      }
      if (WHITESPACE.test(ch) || RESERVED.has(ch)) break;
      wildcard ||= ch === '*' || ch === '?';
      i++;
    }
    if (i === start) {
      // A lone backslash at the very end escapes nothing.
      push('invalid', i, ++i);
      continue;
    }
    const raw = text.slice(start, i);
    if (text[i] === ':' && !escaped && isFieldName(raw)) {
      i++;
      tokens.push({ kind: 'field', start, end: i, value: raw });
      continue;
    }
    const previous = tokens.at(-1);
    let kind: QueryTokenKind;
    if (inRange) kind = !escaped && raw === 'TO' ? 'range-to' : 'range-bound';
    else if (escaped) kind = wildcard ? 'wildcard' : 'term';
    else if (raw === '*' && previous?.kind === 'field' && previous.end === start) kind = 'exists';
    else if (OPERATORS.has(raw)) kind = 'operator';
    else if (PROXIMITY.test(raw)) kind = 'proximity';
    else if (UNSUPPORTED.test(raw)) kind = 'invalid';
    else kind = wildcard ? 'wildcard' : 'term';
    push(kind, start, i, raw);
  }
  return tokens;
}
