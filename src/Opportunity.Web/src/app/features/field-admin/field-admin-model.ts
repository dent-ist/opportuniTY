import type {
  CodingLayoutRequest,
  CodingLayoutResource,
  FieldCapabilitiesResource,
  FieldResource,
} from '../../core/api/generated/models';
import type { AdminLayout } from '../../core/fields/field-admin-api';
import type { CodingLayout } from '../documents/review/review-ports';
import { catalogOf, layoutsOf } from '../documents/review/review-ports';

// Shared model of Admin › Fields, Choices and Coding Layouts (E04-T06). Display names follow the familiarity guide
// §1.1; the API keeps the ADR-003 names.

export type FieldType = FieldResource['type'];

/** Field types in the order the editor offers them, with their display names. */
export const FIELD_TYPES: readonly { readonly value: FieldType; readonly label: string }[] = [
  { value: 'keyword', label: 'Short Text' },
  { value: 'text', label: 'Long Text' },
  { value: 'integer', label: 'Whole Number' },
  { value: 'decimal', label: 'Decimal' },
  { value: 'date', label: 'Date' },
  { value: 'boolean', label: 'Yes/No' },
  { value: 'singleChoice', label: 'Single Choice' },
  { value: 'multiChoice', label: 'Multiple Choice' },
  { value: 'user', label: 'User' },
];

export function typeLabel(type: string, datePrecision?: string | null): string {
  if (type === 'date' && datePrecision === 'dateTime') return 'Date and Time';
  return FIELD_TYPES.find((t) => t.value === type)?.label ?? type;
}

export function isChoiceType(type: string): boolean {
  return type === 'singleChoice' || type === 'multiChoice';
}

/** Where values come from, in words. */
export function storageLabel(storage: string): string {
  return storage === 'coding' ? 'Coding' : storage === 'metadata' ? 'Imported' : 'System';
}

export const SECURITY_CLASSES = [
  { value: 'privilegeStatus', label: 'Privilege status' },
  { value: 'confidentialityDesignation', label: 'Confidentiality designation' },
  { value: 'ethicalWall', label: 'Ethical wall' },
] as const;

/** Workspace roles a layout can be offered to (E05-T02 catalogue, without break-glass access). */
export const LAYOUT_ROLES = [
  { key: 'Reviewer', label: 'Reviewer' },
  { key: 'QcReviewer', label: 'QC Reviewer' },
  { key: 'PrivilegeReviewer', label: 'Privilege Reviewer' },
  { key: 'ProductionManager', label: 'Production Manager' },
  { key: 'Auditor', label: 'Auditor' },
  { key: 'WorkspaceAdmin', label: 'Workspace Admin' },
] as const;

/** What search can do with a field, as short words for the list and editor. */
export function capabilityWords(c: FieldCapabilitiesResource): string[] {
  const words: string[] = [];
  if (c.filterable) words.push('Searchable');
  if (c.sortable) words.push('Sortable');
  if (c.rangeable) words.push('Ranges');
  if (c.aggregatable) words.push('Value counts');
  if (c.fullText) words.push('Full text');
  return words;
}

/** A copy of `items` with the item at `from` moved to `to`. */
export function moved<T>(items: readonly T[], from: number, to: number): T[] {
  const next = [...items];
  if (from < 0 || from >= next.length || to < 0 || to >= next.length) return next;
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  return next;
}

// ── Coding layout drafts ────────────────────────────────────────────────────────────────────────────────────

export interface DraftCondition {
  readonly fieldId: number;
  /** Choice controlling field: shown when it has any of these choices. */
  readonly choiceIds: readonly number[] | null;
  /** Yes/No controlling field: shown when it has this value. */
  readonly booleanValue: boolean | null;
}

export interface DraftField {
  readonly fieldId: number;
  readonly required: boolean;
  readonly readOnly: boolean;
  readonly applyToFamily: boolean;
  readonly condition: DraftCondition | null;
}

export interface DraftSection {
  /** Stable key on screen (track, drop list ids). */
  readonly key: string;
  readonly sectionId: string | null;
  readonly title: string;
  readonly fields: readonly DraftField[];
}

export interface LayoutDraft {
  readonly name: string;
  readonly isDefault: boolean;
  readonly roles: readonly string[];
  readonly sections: readonly DraftSection[];
}

let sectionKeys = 0;

export function newSection(title: string, fields: readonly DraftField[] = []): DraftSection {
  return { key: `section-${++sectionKeys}`, sectionId: null, title, fields };
}

export function newField(fieldId: number, readOnly: boolean): DraftField {
  return { fieldId, required: false, readOnly, applyToFamily: false, condition: null };
}

export function draftOf(layout: AdminLayout | null): LayoutDraft {
  if (!layout) return { name: '', isDefault: false, roles: [], sections: [newSection('Coding')] };
  return {
    name: layout.name,
    isDefault: layout.isDefault,
    roles: [...layout.roles],
    sections: layout.sections.map((s) => ({
      ...newSection(s.title),
      sectionId: s.sectionId,
      fields: s.fields.map((f) => ({
        fieldId: Number(f.fieldId),
        required: f.isRequired,
        readOnly: f.isReadOnly,
        applyToFamily: f.applyToFamilyByDefault ?? false,
        condition: f.visibleWhen
          ? {
              fieldId: Number(f.visibleWhen.fieldId),
              choiceIds: f.visibleWhen.choiceIds?.map(Number) ?? null,
              booleanValue: f.visibleWhen.booleanValue,
            }
          : null,
      })),
    })),
  };
}

export function requestOf(draft: LayoutDraft): CodingLayoutRequest {
  return {
    name: draft.name.trim(),
    isDefault: draft.isDefault,
    roles: [...draft.roles],
    sections: draft.sections.map((s) => ({
      sectionId: s.sectionId,
      title: s.title.trim(),
      fields: s.fields.map((f) => ({
        fieldId: f.fieldId,
        isRequired: f.required,
        isReadOnly: f.readOnly,
        applyToFamilyByDefault: f.applyToFamily,
        visibleWhen: f.condition
          ? {
              fieldId: f.condition.fieldId,
              choiceIds: f.condition.choiceIds ? [...f.condition.choiceIds] : null,
              booleanValue: f.condition.booleanValue,
            }
          : null,
      })),
    })),
  };
}

/**
 * The draft as the coding pane renders it: the same conversion the pane applies to `GET …/coding-layouts`, so the
 * preview is exactly what reviewers see (an empty layout shows every coding field, like the pane).
 */
export function previewOf(draft: LayoutDraft, fields: readonly FieldResource[]): CodingLayout {
  const request = requestOf(draft);
  const resource: CodingLayoutResource = {
    layoutId: 'preview',
    name: request.name || 'New layout',
    isDefault: request.isDefault,
    sections: request.sections.map((s, i) => ({
      sectionId: s.sectionId ?? `preview-${i}`,
      title: s.title || 'Untitled section',
      fields: s.fields,
    })),
  };
  return layoutsOf([resource], catalogOf(fields))[0];
}

/** Why a field cannot be offered "apply to family by default" (Q-48), or null when it can. */
export function applyToFamilyBlocked(
  field: FieldResource | undefined,
  readOnly: boolean,
): string | null {
  if (!field) return null;
  if (field.storage !== 'coding') return 'Only coding fields can be applied to the family.';
  if (field.isSecurityAffecting)
    return 'Privilege, confidentiality and ethical wall fields are never pre-selected for the family.';
  if (readOnly) return 'Read-only fields are not applied to the family.';
  return null;
}

/**
 * Problems the editor can see before saving, keyed like the API (`name`, `sections`, `f<fieldId>`), so server and
 * local messages land in the same places.
 */
export function validateDraft(
  draft: LayoutDraft,
  fields: ReadonlyMap<number, FieldResource>,
): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!draft.name.trim()) errors['name'] = 'Enter a layout name.';
  if (draft.sections.some((s) => !s.title.trim()))
    errors['sections'] = 'Every section needs a title.';
  const placed = new Set(draft.sections.flatMap((s) => s.fields.map((f) => f.fieldId)));
  for (const f of draft.sections.flatMap((s) => s.fields)) {
    const key = `f${f.fieldId}`;
    const field = fields.get(f.fieldId);
    if (f.required && (f.readOnly || field?.storage !== 'coding')) {
      errors[key] = 'Only editable coding fields can be required.';
    }
    const condition = f.condition;
    if (condition) {
      const controlling = fields.get(condition.fieldId);
      if (!placed.has(condition.fieldId)) {
        errors[key] = 'The field it depends on must be in this layout.';
      } else if (controlling?.type === 'boolean' && condition.booleanValue === null) {
        errors[key] = `Choose Yes or No for ${controlling.displayName}.`;
      } else if (
        controlling &&
        isChoiceType(controlling.type) &&
        !(condition.choiceIds && condition.choiceIds.length > 0)
      ) {
        errors[key] = `Choose at least one choice of ${controlling.displayName}.`;
      }
    }
  }
  return errors;
}
