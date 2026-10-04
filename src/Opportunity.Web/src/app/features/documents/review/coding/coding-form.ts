import type { CodingLayout, CodingLayoutField, CodingValue } from '../review-ports';

// The coding pane's form logic (E16-T05), free of Angular so it is tested on its own: which fields show, how a
// value is entered and shown per field type, what blocks a save, and which values a save sends.

/** Choice fields with at most this many choices are radio buttons / checkboxes; longer lists get a filter. */
export const SHORT_CHOICE_LIST = 15;

/** Access digits (1)…(9) for the first nine choices of a field. */
export const ACCESS_DIGITS = 9;

/** No value: null, '', or an empty list. */
export function isEmpty(value: CodingValue | undefined): boolean {
  return (
    value === null ||
    value === undefined ||
    value === '' ||
    (Array.isArray(value) && value.length === 0)
  );
}

/** Equal for coding purposes: empties are equal; lists compare as sets (choice order carries no meaning). */
export function sameValue(a: CodingValue | undefined, b: CodingValue | undefined): boolean {
  if (isEmpty(a) || isEmpty(b)) return isEmpty(a) && isEmpty(b);
  if (Array.isArray(a) || Array.isArray(b)) {
    const x = Array.isArray(a) ? a : [a];
    const y = Array.isArray(b) ? b : [b];
    return x.length === y.length && x.every((v) => y.includes(v));
  }
  return String(a) === String(b);
}

/** Whether a field shows, given the values on screen (its controlling field's condition). */
export function isVisible(
  field: CodingLayoutField,
  value: (queryName: string) => CodingValue | undefined,
): boolean {
  const condition = field.visibleWhen;
  if (!condition) return true;
  const current = value(condition.queryName);
  if (condition.value !== null) return current === condition.value;
  const chosen = Array.isArray(current) ? current : isEmpty(current) ? [] : [String(current)];
  return (condition.choices ?? []).some((c) => chosen.includes(c));
}

/** Every field of a layout in order. */
export function layoutFields(layout: CodingLayout | null): CodingLayoutField[] {
  return layout?.sections.flatMap((s) => s.fields) ?? [];
}

/** How the field is edited. */
export type EditorKind =
  | 'yesNo'
  | 'radio'
  | 'checkbox'
  | 'filterSingle'
  | 'filterMulti'
  | 'text'
  | 'longText'
  | 'lines'
  | 'number'
  | 'date'
  | 'dateTime'
  | 'user';

export function editorKind(field: CodingLayoutField): EditorKind {
  switch (field.type) {
    case 'boolean':
      return 'yesNo';
    case 'singleChoice':
      return field.choices.length > SHORT_CHOICE_LIST ? 'filterSingle' : 'radio';
    case 'multiChoice':
      return field.choices.length > SHORT_CHOICE_LIST ? 'filterMulti' : 'checkbox';
    case 'integer':
    case 'decimal':
      return 'number';
    case 'date':
      return field.datePrecision === 'dateTime' ? 'dateTime' : 'date';
    case 'user':
      return 'user';
    case 'text':
      return field.multiValue ? 'lines' : 'longText';
    default:
      return field.multiValue ? 'lines' : 'text';
  }
}

/** The text a value is shown as (read-only fields, differences). */
export function displayValue(field: CodingLayoutField, value: CodingValue | undefined): string {
  if (isEmpty(value)) return 'Not set';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (Array.isArray(value)) return value.join(', ');
  if (field.type === 'date' && field.datePrecision === 'dateTime') {
    return String(value)
      .replace('T', ' ')
      .replace(/(:\d\d)(\.\d+)?Z$/, '$1 UTC');
  }
  return String(value);
}

/** A `datetime-local` input's value for a stored UTC instant, and back (the pane edits instants in UTC). */
export function toDateTimeInput(value: CodingValue | undefined): string {
  return typeof value === 'string' ? value.replace(/(:\d\d)(\.\d+)?Z$/, '$1').slice(0, 19) : '';
}

export function fromDateTimeInput(text: string): string | null {
  if (!text) return null;
  return /T\d\d:\d\d$/.test(text) ? `${text}:00Z` : `${text}Z`;
}

const INTEGER = /^[+-]?\d+$/;
const DECIMAL = /^[+-]?(\d+([.,]\d*)?|[.,]\d+)$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?Z$/;

/** Why a field's value cannot be saved, or null. Required applies only to fields on screen. */
export function fieldError(
  field: CodingLayoutField,
  value: CodingValue | undefined,
): string | null {
  if (isEmpty(value)) return field.required ? `${field.label} is required.` : null;
  switch (field.type) {
    case 'integer':
      return INTEGER.test(String(value).trim()) &&
        Number.isSafeInteger(Number(String(value).trim()))
        ? null
        : `${field.label}: enter a whole number, for example 42.`;
    case 'decimal':
      return DECIMAL.test(String(value).trim())
        ? null
        : `${field.label}: enter a number, for example 1250.50.`;
    case 'date':
      if (field.datePrecision === 'dateTime') {
        return DATE_TIME.test(String(value)) && !Number.isNaN(Date.parse(String(value)))
          ? null
          : `${field.label}: enter a date and time.`;
      }
      return DATE.test(String(value)) && !Number.isNaN(Date.parse(String(value)))
        ? null
        : `${field.label}: enter a date as YYYY-MM-DD.`;
    default:
      return null;
  }
}

/** Errors of the fields on screen, in layout order (the first is focused). */
export function validate(
  fields: readonly CodingLayoutField[],
  value: (queryName: string) => CodingValue | undefined,
): Map<string, string> {
  const errors = new Map<string, string>();
  for (const field of fields) {
    if (!isVisible(field, value)) continue;
    const error = fieldError(field, value(field.queryName));
    if (error) errors.set(field.queryName, error);
  }
  return errors;
}

/** The value as the API takes it: numbers parsed, empties as null. */
export function apiValue(field: CodingLayoutField, value: CodingValue | undefined): CodingValue {
  if (isEmpty(value)) return null;
  if (field.type === 'integer' || field.type === 'decimal') {
    return Number(String(value).trim().replace(',', '.'));
  }
  if (typeof value === 'string' && field.type !== 'user') return value.trim() || null;
  return value ?? null;
}

/** One value per line, for multi-value text fields. */
export function linesOf(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0);
}

/** Toggles choice `name` in a multi-choice value; picks or clears it in a single-choice value. */
export function toggleChoice(
  field: CodingLayoutField,
  current: CodingValue | undefined,
  name: string,
): CodingValue {
  if (field.type === 'multiChoice') {
    const chosen = Array.isArray(current) ? current : [];
    return chosen.includes(name) ? chosen.filter((c) => c !== name) : [...chosen, name];
  }
  return current === name ? null : name;
}
