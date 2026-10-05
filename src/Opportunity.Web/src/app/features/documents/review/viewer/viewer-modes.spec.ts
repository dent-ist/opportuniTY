import { TextSegments, SEGMENT_CHARS, findPattern, matchOffsets, segmentAt } from './text-segments';
import { documentResource } from './viewer-fixtures.testing';
import { initialMode, modeAvailability } from './viewer-modes';

describe('viewer modes (E16-T04)', () => {
  it('derives availability and reasons from the document flags', () => {
    const availability = modeAvailability(
      documentResource(1, {
        text: { available: false, missing: false },
        images: { available: false, status: 'failed' },
        native: { available: false, missing: true },
      }),
    );
    expect(availability).toEqual({
      text: { available: false, reason: 'No extracted text for this document' },
      image: { available: false, reason: 'Image rendering failed' },
      native: { available: false, reason: 'The native file is missing' },
      production: { available: false, reason: 'Not produced' },
      metadata: { available: true, reason: null },
    });
    expect(
      modeAvailability(documentResource(1, { images: { available: false, status: 'pending' } }))
        .image.reason,
    ).toBe('Image rendering in progress');
  });

  it('opens in the last mode when the document has it, else Image → Extracted Text → Metadata', () => {
    const all = modeAvailability(
      documentResource(1, { images: { available: true, status: 'ready' } }),
    );
    const textOnly = modeAvailability(documentResource(1));
    const none = modeAvailability(
      documentResource(1, { text: { available: false }, native: { available: false } }),
    );
    expect(initialMode(all, null)).toBe('image');
    expect(initialMode(all, 'native')).toBe('native');
    expect(initialMode(textOnly, null)).toBe('text');
    expect(initialMode(textOnly, 'image')).toBe('text');
    expect(initialMode(none, 'text')).toBe('metadata');
    expect(initialMode(all, 'production')).toBe('image');
  });
});

describe('streamed text', () => {
  it('keeps lines whole across chunk boundaries and blocks small', () => {
    const segments = new TextSegments();
    segments.append('first line\nsecond li');
    const all = segments.append('ne continues\nthird\n');
    expect(all.map((s) => s.text)).toEqual(['first line\nsecond line continues\nthird\n']);
    expect(all[0].start).toBe(0);

    const long = new TextSegments().append(`${'word '.repeat(SEGMENT_CHARS)}\n`);
    expect(long.length).toBeGreaterThan(1);
    expect(long.every((s) => s.text.length <= 2 * SEGMENT_CHARS)).toBe(true);
    expect(long.map((s) => s.text).join('')).toBe(`${'word '.repeat(SEGMENT_CHARS)}\n`);
    // Every block knows where it starts, so server hit offsets map onto the rendered text.
    long.forEach((s, i) =>
      expect(s.start).toBe(i === 0 ? 0 : long[i - 1].start + long[i - 1].text.length),
    );
    expect(segmentAt(long, long[1].start)).toBe(1);
    expect(segmentAt(long, long[1].start - 1)).toBe(0);
    expect(segmentAt(long, 10 ** 9)).toBe(-1);
  });

  it('finds literal text, case-insensitively', () => {
    expect(matchOffsets('a.b A.B axb', findPattern('a.b')!, 10)).toEqual([
      { start: 0, end: 3 },
      { start: 4, end: 7 },
    ]);
    expect(findPattern('  ')).toBeNull();
  });
});
