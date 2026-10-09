import { relatedDocumentsParams } from '../../documents/relationships/relationship-link';
import type {
  CodedValue,
  ConflictGroup,
  ConflictMember,
  ConflictReason,
} from './privilege-conflicts-api';

// Pure helpers of the Privilege Conflicts screen (E13-T02), tested without a browser.

export const REASON_LABELS: Record<ConflictReason, string> = {
  withheldMember: 'Withheld member',
  responsivenessDiffers: 'Responsiveness differs',
  privilegeCallsDiffer: 'Privilege calls differ',
};

export const REASON_DESCRIPTIONS: Record<ConflictReason, string> = {
  withheldMember:
    'A family member is coded Withhold and another is not, so producing the family would leave it incomplete or misleading.',
  responsivenessDiffers: 'Family members have different responsiveness calls.',
  privilegeCallsDiffer: 'These duplicates have different privilege calls.',
};

/** The privilege call that "Not privileged" stands for (the built-in choice name). */
const NOT_PRIVILEGED = 'Not Privileged';

/** "Family of ACM0001" / "Duplicates of ACM0007": named after the parent or primary when it is listed. */
export function groupTitle(group: ConflictGroup): string {
  const head =
    group.kind === 'family'
      ? group.members.find((m) => m.familySequence === 0)
      : group.members.find((m) => m.isPrimary);
  const first = head ?? group.members[0];
  const what = group.kind === 'family' ? 'Family' : 'Duplicates';
  return `${what} ${head ? 'of' : 'with'} ${first?.controlNumber ?? ''}`.trim();
}

/** The document whose control number names the group in links and messages. */
export function groupAnchor(group: ConflictGroup): ConflictMember | undefined {
  return (
    (group.kind === 'family'
      ? group.members.find((m) => m.familySequence === 0)
      : group.members.find((m) => m.isPrimary)) ?? group.members[0]
  );
}

/** Query parameters of "Open in Documents" for the group. */
export function documentsParams(group: ConflictGroup): Record<string, string> {
  return relatedDocumentsParams(group.kind, group.groupId, groupAnchor(group)?.controlNumber ?? '');
}

/**
 * The member whose call a duplicate group most likely should carry, preselected as the source of a propagation: the
 * primary when it has a privilege call other than Not Privileged, else the first member with such a call, else the
 * first member with any call. Null when no member is coded.
 */
export function defaultSource(group: ConflictGroup): string | null {
  const privileged = (m: ConflictMember) =>
    m.status.values.length > 0 && !m.status.values.includes(NOT_PRIVILEGED);
  const primary = group.members.find((m) => m.isPrimary && privileged(m));
  const chosen =
    primary ??
    group.members.find(privileged) ??
    group.members.find((m) => m.status.values.length > 0);
  return chosen?.documentId ?? null;
}

/** A value as one line of text; empty when there is none (the template says "Not coded"). */
export function valueText(value: CodedValue | null): string {
  return value ? value.values.join(', ') : '';
}

/** "3 family conflicts · 1 duplicate conflict". */
export function summaryText(families: number, duplicates: number, locale: string): string {
  const n = new Intl.NumberFormat(locale);
  const part = (count: number, one: string, many: string) =>
    `${n.format(count)} ${count === 1 ? one : many}`;
  return `${part(families, 'family conflict', 'family conflicts')} · ${part(duplicates, 'duplicate conflict', 'duplicate conflicts')}`;
}

/** The confirmation text of "Propagate privilege call…" for `groups` selected groups with `documents` other members. */
export function propagationMessage(groups: number, documents: number): string {
  const g = groups === 1 ? '1 duplicate group' : `${groups} duplicate groups`;
  const d = documents === 1 ? '1 other listed document' : `${documents} other listed documents`;
  return (
    `Privilege Status and Privilege Basis of the chosen document are copied to the other documents of ${g} ` +
    `(${d}, and any other member you may code). It runs as a job; a document someone else changes after you start ` +
    `is left alone and listed in the job. Each change records which coding it was copied from.`
  );
}
