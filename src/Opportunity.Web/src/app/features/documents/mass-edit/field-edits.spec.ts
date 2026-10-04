import type { FieldResource } from '../../../core/api/generated/models';
import {
  MAX_SELECTED_IDS,
  rangeBetween,
  selectionLabel,
  selectionProblem,
} from '../grid/selection';
import {
  NO_EDIT,
  describeEdit,
  editProblem,
  editableFields,
  matchesConfirmation,
  toChanges,
} from './field-edits';

function field(id: number, type: FieldResource['type'], extra: Partial<FieldResource> = {}) {
  return {
    fieldId: id,
    queryName: `f${id}`,
    displayName: `Field ${id}`,
    type,
    storage: 'coding',
    multiValue: type === 'multiChoice',
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    reducedCapabilities: false,
    capabilities: {} as FieldResource['capabilities'],
    choices: null,
    ...extra,
  } as FieldResource;
}

const ISSUES = field(2, 'multiChoice', {
  displayName: 'Issues',
  choices: [
    { choiceId: 11, name: 'Pricing', isActive: true },
    { choiceId: 12, name: 'Termination', isActive: true },
    { choiceId: 13, name: 'Retired', isActive: false },
  ],
});
const RESPONSIVENESS = field(1, 'singleChoice', {
  displayName: 'Responsiveness',
  choices: [{ choiceId: 1, name: 'Responsive', isActive: true }],
});

describe('Mass Edit field edits', () => {
  it('offers visible coding fields only, with active choices, and locks security fields without privilege', () => {
    const fields = editableFields(
      [
        RESPONSIVENESS,
        ISSUES,
        field(3, 'keyword', { storage: 'column' }),
        field(4, 'text', { isHidden: true }),
        field(5, 'user'),
        field(6, 'singleChoice', { isSecurityAffecting: true }),
      ],
      false,
    );
    expect(fields.map((f) => f.fieldId)).toEqual(['1', '2', '6']);
    expect(fields[1].choices.map((c) => c.name)).toEqual(['Pricing', 'Termination']);
    expect(fields.map((f) => f.locked)).toEqual([false, false, true]);
    expect(
      editableFields([field(6, 'singleChoice', { isSecurityAffecting: true })], true)[0].locked,
    ).toBe(false);
  });

  it('turns edits into set / addChoices / removeChoices / clear operations with typed ids and values', () => {
    const [resp, issues] = editableFields([RESPONSIVENESS, ISSUES], true);
    const [count, date, flag] = editableFields(
      [field(7, 'integer'), field(8, 'date'), field(9, 'boolean')],
      true,
    );
    expect(toChanges(resp, NO_EDIT)).toBeNull();
    expect(toChanges(resp, { ...NO_EDIT, change: true, value: '1' })).toEqual([
      { fieldId: '1', operation: 'set', value: 1 },
    ]);
    expect(toChanges(resp, { ...NO_EDIT, change: true, mode: 'clear' })).toEqual([
      { fieldId: '1', operation: 'clear' },
    ]);
    expect(
      toChanges(issues, {
        ...NO_EDIT,
        change: true,
        mode: 'addRemove',
        choices: { '11': 'add', '12': 'remove' },
      }),
    ).toEqual([
      { fieldId: '2', operation: 'addChoices', value: [11] },
      { fieldId: '2', operation: 'removeChoices', value: [12] },
    ]);
    expect(
      toChanges(issues, { ...NO_EDIT, change: true, mode: 'replace', replace: ['12'] }),
    ).toEqual([{ fieldId: '2', operation: 'set', value: [12] }]);
    expect(toChanges(count, { ...NO_EDIT, change: true, value: ' 42 ' })![0].value).toBe(42);
    expect(toChanges(date, { ...NO_EDIT, change: true, value: '2024-03-01' })![0].value).toBe(
      '2024-03-01',
    );
    expect(toChanges(flag, { ...NO_EDIT, change: true, value: 'false' })![0].value).toBe(false);
  });

  it('reports incomplete edits and describes the changes for the confirmation', () => {
    const [resp, issues] = editableFields([RESPONSIVENESS, ISSUES], true);
    const [count] = editableFields([field(7, 'integer')], true);
    expect(editProblem(resp, { ...NO_EDIT, change: true })).toBe('Choose a value.');
    expect(editProblem(issues, { ...NO_EDIT, change: true, mode: 'addRemove' })).toMatch(
      /at least one/,
    );
    expect(editProblem(issues, { ...NO_EDIT, change: true, mode: 'replace' })).toMatch(/replace/);
    expect(editProblem(count, { ...NO_EDIT, change: true, value: '1.5' })).toBe(
      'Enter a whole number.',
    );
    expect(editProblem(count, { ...NO_EDIT, change: true, mode: 'clear' })).toBeNull();

    expect(describeEdit(resp, { ...NO_EDIT, change: true, value: '1' })).toBe(
      'Responsiveness: set to Responsive',
    );
    expect(
      describeEdit(issues, {
        ...NO_EDIT,
        change: true,
        mode: 'addRemove',
        choices: { '11': 'add', '12': 'remove' },
      }),
    ).toBe('Issues: add Pricing; remove Termination');
    expect(describeEdit(issues, { ...NO_EDIT, change: true, mode: 'clear' })).toBe(
      'Issues: clear the value',
    );
  });

  it('accepts the typed count with or without separators', () => {
    expect(matchesConfirmation('58330', 58_330)).toBe(true);
    expect(matchesConfirmation('58,330', 58_330)).toBe(true);
    expect(matchesConfirmation('58 330', 58_330)).toBe(true);
    expect(matchesConfirmation('5833', 58_330)).toBe(false);
  });
});

describe('Selection model', () => {
  it('labels, checks and ranges selections; all results are never an id list', () => {
    expect(selectionLabel(null, 'en')).toBe('0');
    expect(selectionLabel({ kind: 'documents', documentIds: ['a', 'b'] }, 'en')).toBe('2');
    const all = {
      kind: 'all',
      query: 'responsiveness:"Responsive"',
      generation: '18432',
      total: { value: 10_000, relation: 'gte' },
      countText: '≥ 10,000 (approx.)',
    } as const;
    expect(selectionLabel(all, 'en')).toBe('all ≥ 10,000 (approx.) results');
    expect(selectionProblem(all)).toBeNull();
    expect(selectionProblem(null)).toMatch(/Select documents first/);
    const many = Array.from({ length: MAX_SELECTED_IDS + 1 }, (_, i) => `doc-${i}`);
    expect(selectionProblem({ kind: 'documents', documentIds: many })).toMatch(
      /Select all results/,
    );
    expect(rangeBetween(5, 2)).toEqual([2, 3, 4, 5]);
  });
});
