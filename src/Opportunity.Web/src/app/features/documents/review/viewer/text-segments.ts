/** One block of rendered text; the browser lays out only the blocks near the viewport (`content-visibility`). */
export interface TextSegment {
  readonly id: number;
  /** Offset of the block's first character in the whole text (UTF-16 code units). */
  readonly start: number;
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
  private length = 0;

  get all(): readonly TextSegment[] {
    return this.segments;
  }

  /** Adds the next chunk of text; returns the new list (a new array, for signals). */
  append(text: string): readonly TextSegment[] {
    let rest = text;
    let start = this.length;
    const last = this.segments.at(-1);
    if (last && !last.text.endsWith('\n') && last.text.length < SEGMENT_CHARS) {
      // The previous block ended inside a line: reopen it so the line is not broken in two.
      this.segments = this.segments.slice(0, -1);
      rest = last.text + rest;
      start = last.start;
    } else {
      this.segments = [...this.segments];
    }
    this.length += text.length;
    while (rest.length > 0) {
      const cut = cutPoint(rest);
      this.segments.push({ id: this.nextId++, start, text: rest.slice(0, cut) });
      start += cut;
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

export function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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

/**
 * The index of the block holding text offset `offset` (blocks are in order and contiguous), or -1 when the offset
 * lies beyond the loaded text.
 */
export function segmentAt(segments: readonly TextSegment[], offset: number): number {
  let low = 0;
  let high = segments.length - 1;
  while (low <= high) {
    const mid = (low + high) >> 1;
    const segment = segments[mid];
    if (offset < segment.start) high = mid - 1;
    else if (offset >= segment.start + segment.text.length) low = mid + 1;
    else return mid;
  }
  return -1;
}
