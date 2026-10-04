import type { FieldResource } from '../../../core/api/generated/models';
import type { BulkFieldChange } from './bulk-coding-api';

/** Q-34: typed confirmation above this many documents (and for any security-affecting field). */
export const TYPED_CONFIRMATION_THRESHOLD = 10_000;

/** What happens to one field. Unchecked "Change" leaves the field untouched. */
export type EditMode = 'set' | 'clear' | 'addRemove' | 'replace';

/** Per choice of a Multiple Choice field. */
export type ChoiceAction = 'keep' | 'add' | 'remove';

export interface FieldEdit {
  readonly change: boolean;
  readonly mode: EditMode;
  /** `set` of a single-value field: text, number, date (yyyy-mm-dd), 'true'/'false' or a choice id. */
  readonly value: string;
  /** `addRemove` of a Multiple Choice field, by choice id. */
  readonly choices: Readonly<Record<string, ChoiceAction>>;
  /** `replace` of a Multiple Choice field: the choice ids that become the whole value. */
  readonly replace: readonly string[];
}

export const NO_EDIT: FieldEdit = {
  change: false,
  mode: 'set',
  value: '',
  choices: {},
  replace: [],
};

export interface EditableField {
  readonly field: FieldResource;
  readonly fieldId: string;
  /** Security-affecting fields need Coding.WritePrivilege (Q-11). */
  readonly locked: boolean;
  readonly choices: readonly { readonly id: string; readonly name: string }[];
}

const SUPPORTED = new Set([
  'text',
  'keyword',
  'integer',
  'decimal',
  'date',
  'boolean',
  'singleChoice',
  'multiChoice',
]);

/** Coding fields a Mass Edit can change, in catalogue order (the coding layout API is not in the client yet). */
export function editableFields(
  fields: readonly FieldResource[],
  canWritePrivilege: boolean,
): EditableField[] {
  return fields
    .filter((f) => f.storage === 'coding' && !f.isHidden && SUPPORTED.has(f.type))
    .map((field) => ({
      field,
      fieldId: String(field.fieldId),
      locked: field.isSecurityAffecting && !canWritePrivilege,
      choices: (field.choices ?? [])
        .filter((c) => c.isActive)
        .map((c) => ({ id: String(c.choiceId), name: c.name })),
    }));
}

/** The changes to send, or null when this field is not changed. */
export function toChanges(f: EditableField, edit: FieldEdit): BulkFieldChange[] | null {
  if (!edit.change) return null;
  const fieldId = f.fieldId;
  if (edit.mode === 'clear') return [{ fieldId, operation: 'clear' }];
  if (f.field.type === 'multiChoice') {
    if (edit.mode === 'replace')
      return [{ fieldId, operation: 'set', value: choiceIds(f, edit.replace) }];
    const pick = (action: ChoiceAction) =>
      f.choices.filter((c) => edit.choices[c.id] === action).map((c) => c.id);
    const changes: BulkFieldChange[] = [];
    if (pick('add').length)
      changes.push({ fieldId, operation: 'addChoices', value: choiceIds(f, pick('add')) });
    if (pick('remove').length) {
      changes.push({ fieldId, operation: 'removeChoices', value: choiceIds(f, pick('remove')) });
    }
    return changes;
  }
  return [{ fieldId, operation: 'set', value: typedValue(f, edit.value) }];
}

/** Why a changed field cannot be applied as entered, or null. */
export function editProblem(f: EditableField, edit: FieldEdit): string | null {
  if (!edit.change || edit.mode === 'clear') return null;
  const type = f.field.type;
  if (type === 'multiChoice') {
    if (edit.mode === 'replace') {
      return edit.replace.length
        ? null
        : 'Choose the choices that replace the current values, or choose Clear.';
    }
    return Object.values(edit.choices).some((a) => a !== 'keep')
      ? null
      : 'Choose at least one choice to add or remove.';
  }
  const value = edit.value.trim();
  if (!value)
    return type === 'singleChoice' || type === 'boolean' ? 'Choose a value.' : 'Enter a value.';
  if ((type === 'integer' || type === 'decimal') && !Number.isFinite(Number(value))) {
    return 'Enter a number.';
  }
  if (type === 'integer' && !Number.isInteger(Number(value))) return 'Enter a whole number.';
  if (type === 'date' && !/^\d{4}-\d{2}-\d{2}$/.test(value)) return 'Enter a date.';
  return null;
}

/** One line of the confirmation's change summary: "Responsiveness: set to Responsive". */
export function describeEdit(f: EditableField, edit: FieldEdit): string {
  const name = f.field.displayName;
  if (edit.mode === 'clear') return `${name}: clear the value`;
  const names = (ids: readonly string[]) =>
    ids.map((id) => f.choices.find((c) => c.id === id)?.name ?? id).join(', ');
  if (f.field.type === 'multiChoice') {
    if (edit.mode === 'replace') return `${name}: replace all values with ${names(edit.replace)}`;
    const add = f.choices.filter((c) => edit.choices[c.id] === 'add').map((c) => c.id);
    const remove = f.choices.filter((c) => edit.choices[c.id] === 'remove').map((c) => c.id);
    return [
      add.length ? `${name}: add ${names(add)}` : '',
      remove.length ? `${add.length ? '; ' : `${name}: `}remove ${names(remove)}` : '',
    ].join('');
  }
  if (f.field.type === 'singleChoice') return `${name}: set to ${names([edit.value])}`;
  if (f.field.type === 'boolean') return `${name}: set to ${edit.value === 'true' ? 'Yes' : 'No'}`;
  return `${name}: set to “${edit.value.trim()}”`;
}

/** Choice ids as the catalogue types them (numbers stay numbers). */
function choiceIds(f: EditableField, ids: readonly string[]): (string | number)[] {
  return ids.map((id) => {
    const choice = f.field.choices?.find((c) => String(c.choiceId) === id);
    return choice ? choice.choiceId : id;
  });
}

function typedValue(f: EditableField, raw: string): unknown {
  const value = raw.trim();
  switch (f.field.type) {
    case 'integer':
    case 'decimal':
      return Number(value);
    case 'boolean':
      return value === 'true';
    case 'singleChoice':
      return choiceIds(f, [value])[0];
    default:
      return value;
  }
}

/** The text a typed confirmation expects: the frozen count without separators ("58330"). */
export function confirmationText(count: number): string {
  return String(count);
}

/** Typed input matches the count, with or without thousands separators. */
export function matchesConfirmation(typed: string, count: number): boolean {
  return typed.replace(/[\s,.'’  ]/g, '') === confirmationText(count);
}
