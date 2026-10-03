import { QueryToken, QueryTokenKind } from './query-lexer';

export interface Span {
  readonly start: number;
  readonly end: number;
}

export interface HighlightSegment {
  readonly text: string;
  readonly kind: QueryTokenKind | null;
  readonly error: boolean;
  readonly warning: boolean;
}

/**
 * Splits `text` into runs for the highlight layer: token kind from the client lexer, error and warning
 * marks from the validate endpoint's spans. An empty error span (e.g. a missing `)` at the end) becomes an
 * empty error segment the layer renders as a visible marker.
 */
export function highlight(
  text: string,
  tokens: readonly QueryToken[],
  errors: readonly Span[] = [],
  warnings: readonly Span[] = [],
): HighlightSegment[] {
  const clamp = (n: number) => Math.max(0, Math.min(text.length, n));
  const errs = errors.map((s) => ({ start: clamp(s.start), end: clamp(s.end) }));
  const warns = warnings.map((s) => ({ start: clamp(s.start), end: clamp(s.end) }));
  const cuts = new Set<number>([0, text.length]);
  for (const s of [...tokens, ...errs, ...warns]) cuts.add(clamp(s.start)).add(clamp(s.end));
  const points = [...cuts].sort((a, b) => a - b);
  const covers = (spans: readonly Span[], from: number, to: number) =>
    spans.some((s) => s.start <= from && to <= s.end && s.start < s.end);

  const segments: HighlightSegment[] = [];
  for (let i = 0; i < points.length; i++) {
    const from = points[i];
    if (errs.some((s) => s.start === from && s.end === from)) {
      segments.push({ text: '', kind: null, error: true, warning: false });
    }
    const to = points[i + 1];
    if (to === undefined || to === from) continue;
    const token = tokens.find((t) => t.start <= from && to <= t.end);
    segments.push({
      text: text.slice(from, to),
      kind: token?.kind ?? null,
      error: covers(errs, from, to),
      warning: covers(warns, from, to),
    });
  }
  return segments;
}

/** 1-based line and column of a UTF-16 offset, for messages ("line 2, column 7"). */
export function lineAndColumn(text: string, offset: number): { line: number; column: number } {
  const before = text.slice(0, Math.max(0, Math.min(text.length, offset)));
  const lines = before.split('\n');
  return { line: lines.length, column: lines[lines.length - 1].length + 1 };
}
