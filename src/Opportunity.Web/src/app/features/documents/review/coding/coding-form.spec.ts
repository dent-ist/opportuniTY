import type { CodingLayoutField } from '../review-ports';
import {
  apiValue,
  displayValue,
  editorKind,
  fieldError,
  fromDateTimeInput,
  isVisible,
  sameValue,
  toDateTimeInput,
  toggleChoice,
  validate,
} from './coding-form';

const f = (over: Partial<CodingLayoutField>): CodingLayoutField => ({
  queryName: 'q',
  label: 'Field',
  type: 'text',
  multiValue: false,
  securityAffecting: false,
  datePrecision: null,
  choices: [],
  required: false,
  readOnly: false,
  visibleWhen: null,
  ...over,
});
const choices = (n: number) =>
  Array.from({ length: n }, (_, i) => ({ name: `C${i}`, active: true }));

describe('coding form logic (E16-T05)', () => {
  it('picks an editor per field type; short choice lists are radios or checkboxes, long ones filterable', () => {
    expect(editorKind(f({ type: 'boolean' }))).toBe('yesNo');
    expect(editorKind(f({ type: 'singleChoice', choices: choices(15) }))).toBe('radio');
    expect(editorKind(f({ type: 'singleChoice', choices: choices(16) }))).toBe('filterSingle');
    expect(editorKind(f({ type: 'multiChoice', choices: choices(15) }))).toBe('checkbox');
    expect(editorKind(f({ type: 'multiChoice', choices: choices(16) }))).toBe('filterMulti');
    expect(editorKind(f({ type: 'text' }))).toBe('longText');
    expect(editorKind(f({ type: 'keyword' }))).toBe('text');
    expect(editorKind(f({ type: 'keyword', multiValue: true }))).toBe('lines');
    expect(editorKind(f({ type: 'integer' }))).toBe('number');
    expect(editorKind(f({ type: 'decimal' }))).toBe('number');
    expect(editorKind(f({ type: 'date', datePrecision: 'date' }))).toBe('date');
    expect(editorKind(f({ type: 'date', datePrecision: 'dateTime' }))).toBe('dateTime');
    expect(editorKind(f({ type: 'user' }))).toBe('user');
  });

  it('shows a field only while its condition holds', () => {
    const basis = f({
      queryName: 'basis',
      visibleWhen: { queryName: 'status', choices: ['Withhold', 'Redact'], value: null },
    });
    expect(isVisible(basis, () => 'Withhold')).toBe(true);
    expect(isVisible(basis, () => 'Not Privileged')).toBe(false);
    expect(isVisible(basis, () => undefined)).toBe(false);
    expect(isVisible(basis, () => ['Redact'])).toBe(true);
    const notes = f({ visibleWhen: { queryName: 'key', choices: null, value: true } });
    expect(isVisible(notes, () => true)).toBe(true);
    expect(isVisible(notes, () => false)).toBe(false);
  });

  it('validates required fields on screen and values per type', () => {
    const required = f({ queryName: 'resp', label: 'Responsiveness', required: true });
    const hidden = f({
      queryName: 'basis',
      required: true,
      visibleWhen: { queryName: 'resp', choices: ['X'], value: null },
    });
    const errors = validate([required, hidden], () => null);
    expect([...errors]).toEqual([['resp', 'Responsiveness is required.']]);
    expect(fieldError(f({ type: 'integer', label: 'Pages' }), '12')).toBeNull();
    expect(fieldError(f({ type: 'integer', label: 'Pages' }), '1.5')).toMatch(/whole number/);
    expect(fieldError(f({ type: 'decimal', label: 'Hours' }), '1.25')).toBeNull();
    expect(fieldError(f({ type: 'decimal', label: 'Hours' }), 'abc')).toMatch(/number/);
    expect(fieldError(f({ type: 'date', datePrecision: 'date' }), '2024-02-30')).toBeNull();
    expect(fieldError(f({ type: 'date', datePrecision: 'date' }), '2024-2-3')).toMatch(
      /YYYY-MM-DD/,
    );
    expect(
      fieldError(f({ type: 'date', datePrecision: 'dateTime' }), '2024-05-01T10:00:00Z'),
    ).toBeNull();
  });

  it('compares, converts and shows values', () => {
    expect(sameValue(['a', 'b'], ['b', 'a'])).toBe(true);
    expect(sameValue(null, '')).toBe(true);
    expect(sameValue([], undefined)).toBe(true);
    expect(sameValue('a', 'b')).toBe(false);
    expect(apiValue(f({ type: 'integer' }), ' 42 ')).toBe(42);
    expect(apiValue(f({ type: 'decimal' }), '1,5')).toBe(1.5);
    expect(apiValue(f({ type: 'text' }), '   ')).toBeNull();
    expect(apiValue(f({ type: 'multiChoice' }), [])).toBeNull();
    expect(displayValue(f({ type: 'boolean' }), true)).toBe('Yes');
    expect(displayValue(f({ type: 'multiChoice' }), ['A', 'B'])).toBe('A, B');
    expect(displayValue(f({}), null)).toBe('Not set');
    expect(fromDateTimeInput('2024-05-01T10:30')).toBe('2024-05-01T10:30:00Z');
    expect(toDateTimeInput('2024-05-01T10:30:00.000Z')).toBe('2024-05-01T10:30:00');
  });

  it('toggles choices: add and remove in a multi-choice field, pick and clear in a single choice', () => {
    const multi = f({ type: 'multiChoice' });
    expect(toggleChoice(multi, ['A'], 'B')).toEqual(['A', 'B']);
    expect(toggleChoice(multi, ['A', 'B'], 'A')).toEqual(['B']);
    const single = f({ type: 'singleChoice' });
    expect(toggleChoice(single, 'A', 'B')).toBe('B');
    expect(toggleChoice(single, 'B', 'B')).toBeNull();
  });
});
