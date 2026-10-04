import {
  TextSegments,
  SEGMENT_CHARS,
  findPattern,
  matchOffsets,
  snippetTerms,
  termsPattern,
} from './text-segments';
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

    const long = new TextSegments().append(`${'word '.repeat(SEGMENT_CHARS)}\n`);
    expect(long.length).toBeGreaterThan(1);
    expect(long.every((s) => s.text.length <= 2 * SEGMENT_CHARS)).toBe(true);
    expect(long.map((s) => s.text).join('')).toBe(`${'word '.repeat(SEGMENT_CHARS)}\n`);
  });

  it('finds literal text and whole-word terms, case-insensitively', () => {
    expect(matchOffsets('a.b A.B axb', findPattern('a.b')!, 10)).toEqual([
      { start: 0, end: 3 },
      { start: 4, end: 7 },
    ]);
    expect(findPattern('  ')).toBeNull();
    const terms = termsPattern(['term', 'Terminate']);
    expect(matchOffsets('term terminate terms Term', terms!, 10)).toEqual([
      { start: 0, end: 4 },
      { start: 5, end: 14 },
      { start: 21, end: 25 },
    ]);
    expect(
      snippetTerms([
        { text: 'may Terminate the deal', highlights: [{ start: 4, end: 13 }] },
        { text: 'terminate now', highlights: [{ start: 0, end: 9 }] },
      ]),
    ).toEqual(['terminate']);
  });
});
