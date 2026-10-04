/** One block of rendered text; the browser lays out only the blocks near the viewport (`content-visibility`). */
export interface TextSegment {
  readonly id: number;
  readonly text: string;
}

/** Target block size in characters: small enough that laying one out never blocks, large enough to stay few. */
export const SEGMENT_CHARS = 8 * 1024;

/**
 * Splits streamed text into blocks at line ends, so a block boundary is invisible: a block ends with its `\n`,
 * and a line that continues in the next chunk is kept open and completed when that chunk arrives. A line longer
 * than two blocks is cut at a space (or, without one, anywhere).
 */
export class TextSegments {
  private segments: TextSegment[] = [];
  private nextId = 0;

  get all(): readonly TextSegment[] {
    return this.segments;
  }

  /** Adds the next chunk of text; returns the new list (a new array, for signals). */
  append(text: string): readonly TextSegment[] {
    let rest = text;
    const last = this.segments.at(-1);
    if (last && !last.text.endsWith('\n') && last.text.length < SEGMENT_CHARS) {
      // The previous block ended inside a line: reopen it so the line is not broken in two.
      this.segments = this.segments.slice(0, -1);
      rest = last.text + rest;
    } else {
      this.segments = [...this.segments];
    }
    while (rest.length > 0) {
      const cut = cutPoint(rest);
      this.segments.push({ id: this.nextId++, text: rest.slice(0, cut) });
      rest = rest.slice(cut);
    }
    return this.segments;
  }
}

function cutPoint(text: string): number {
  if (text.length <= SEGMENT_CHARS) return text.length;
  const newline = text.lastIndexOf('\n', SEGMENT_CHARS);
  if (newline > 0) return newline + 1;
  const nextNewline = text.indexOf('\n', SEGMENT_CHARS);
  if (nextNewline >= 0 && nextNewline < 2 * SEGMENT_CHARS) return nextNewline + 1;
  const space = text.lastIndexOf(' ', SEGMENT_CHARS);
  return space > 0 ? space + 1 : SEGMENT_CHARS;
}

/** A case-insensitive pattern for a literal find text; null for an empty one. */
export function findPattern(query: string): RegExp | null {
  const q = query.trim();
  return q ? new RegExp(escapeRegExp(q), 'giu') : null;
}

/** Whole-word, case-insensitive pattern for highlight terms (search hits); null without terms. */
export function termsPattern(terms: readonly string[]): RegExp | null {
  const unique = [...new Set(terms.map((t) => t.trim().toLowerCase()).filter(Boolean))];
  if (!unique.length) return null;
  // Longest first, so "terminate" wins over "term" where both match.
  unique.sort((a, b) => b.length - a.length);
  return new RegExp(
    `(?<![\\p{L}\\p{N}])(?:${unique.map(escapeRegExp).join('|')})(?![\\p{L}\\p{N}])`,
    'giu',
  );
}

export function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The highlighted words of a search hit's snippets (`SearchSnippet.highlights` spans of the snippet text). */
export function snippetTerms(
  snippets: readonly { text: string; highlights: readonly { start: unknown; end: unknown }[] }[],
): string[] {
  const terms = new Set<string>();
  for (const snippet of snippets) {
    for (const span of snippet.highlights) {
      const term = snippet.text.slice(Number(span.start), Number(span.end)).trim();
      if (term) terms.add(term.toLowerCase());
    }
  }
  return [...terms];
}

/** Text positions of every match of `pattern` in `text`, at most `limit`. */
export function matchOffsets(
  text: string,
  pattern: RegExp,
  limit: number,
): { start: number; end: number }[] {
  const found: { start: number; end: number }[] = [];
  pattern.lastIndex = 0;
  for (let m = pattern.exec(text); m && found.length < limit; m = pattern.exec(text)) {
    if (m[0].length === 0) {
      pattern.lastIndex++;
      continue;
    }
    found.push({ start: m.index, end: m.index + m[0].length });
  }
  return found;
}
