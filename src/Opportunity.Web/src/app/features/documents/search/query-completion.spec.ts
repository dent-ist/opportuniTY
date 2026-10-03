import { completionContext, quoteValue, suggest } from './query-completion';
import { highlight, lineAndColumn } from './query-highlight';
import { tokenize } from './query-lexer';
import { TEST_FIELDS } from './search-fixtures.testing';

describe('query completion', () => {
  it('offers field names for a word being typed, matching query and display names', () => {
    const context = completionContext('contract AND resp', 17);
    expect(context).toEqual({ kind: 'field', prefix: 'resp', from: 13, to: 17 });
    const items = suggest(context!, TEST_FIELDS);
    expect(items.map((s) => s.insert)).toEqual(['responsiveness:']);
    expect(items[0].detail).toBe('Responsiveness · Single choice');
    expect(items[0].thenValuesOf?.queryName).toBe('responsiveness');

    // "status" matches a word of the display name "Privilege Status".
    expect(suggest(completionContext('stat', 4)!, TEST_FIELDS).map((s) => s.label)).toEqual([
      'privilege_status',
    ]);
  });

  it('offers nothing in operators, phrases, ranges or whitespace unless asked', () => {
    expect(completionContext('a AND', 5)).toBeNull();
    expect(completionContext('"cont', 5)).toBeNull();
    expect(completionContext('date:[2025 TO 20', 16)).toBeNull();
    expect(completionContext('a ', 2)).toBeNull();
    expect(completionContext('a ', 2, true)).toEqual({ kind: 'field', prefix: '', from: 2, to: 2 });
  });

  it('offers SingleChoice and MultiChoice values after field:, quoting where needed', () => {
    const context = completionContext('responsiveness:not', 18);
    expect(context).toEqual({
      kind: 'value',
      field: 'responsiveness',
      prefix: 'not',
      from: 15,
      to: 18,
    });
    expect(suggest(context!, TEST_FIELDS).map((s) => s.insert)).toEqual(['"Not Responsive"']);

    const issues = suggest(completionContext('Issues:', 7)!, TEST_FIELDS);
    expect(issues.map((s) => s.insert)).toEqual(['Pricing', '"Termination \\"for cause\\""', '*']);
    expect(issues.at(-1)?.detail).toBe('Has any value (is set)');

    const partialPhrase = completionContext('privilege_status:"with', 22)!;
    expect(suggest(partialPhrase, TEST_FIELDS).map((s) => s.label)).toEqual(['Withhold']);
    expect(
      suggest(completionContext('key_document:', 13)!, TEST_FIELDS).map((s) => s.label),
    ).toEqual(['yes', 'no', '*']);
  });

  it('replaces a whole field name when the caret is inside it', () => {
    expect(completionContext('cust:smith', 2)).toEqual({
      kind: 'field',
      prefix: 'cu',
      from: 0,
      to: 5,
    });
  });

  it('quotes values that would not be one literal term', () => {
    expect(quoteValue('Responsive')).toBe('Responsive');
    expect(quoteValue('HIGHLY CONFIDENTIAL – AEO')).toBe('"HIGHLY CONFIDENTIAL – AEO"');
    expect(quoteValue('OR')).toBe('"OR"');
    expect(quoteValue('-1')).toBe('"-1"');
    expect(quoteValue('50%*')).toBe('"50%\\*"');
  });
});

describe('query highlight', () => {
  it('splits text into token runs with error and warning marks', () => {
    const text = 'custodian:smith and "open';
    const segments = highlight(
      text,
      tokenize(text),
      [{ start: 20, end: 25 }],
      [{ start: 16, end: 19 }],
    );
    expect(segments.map((s) => [s.text, s.kind, s.error, s.warning])).toEqual([
      ['custodian:', 'field', false, false],
      ['smith', 'term', false, false],
      [' ', null, false, false],
      ['and', 'term', false, true],
      [' ', null, false, false],
      ['"open', 'phrase', true, false],
    ]);
    expect(segments.map((s) => s.text).join('')).toBe(text);
  });

  it('marks an empty error span (missing operand at the end) with an empty segment', () => {
    const segments = highlight('contract AND', tokenize('contract AND'), [{ start: 12, end: 12 }]);
    expect(segments.at(-1)).toEqual({ text: '', kind: null, error: true, warning: false });
  });

  it('reports 1-based positions', () => {
    expect(lineAndColumn('ab\ncd', 4)).toEqual({ line: 2, column: 2 });
    expect(lineAndColumn('abc', 0)).toEqual({ line: 1, column: 1 });
  });
});
