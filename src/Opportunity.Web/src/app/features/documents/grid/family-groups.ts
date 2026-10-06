import type { SearchHit } from '../../../core/api/generated/models';

/**
 * Relationship attributes a search hit may carry (wave-12 contract: additive and nullable on `SearchHit`, #88). Read
 * tolerantly until the generated client has them.
 */
export interface RelationshipHit extends SearchHit {
  readonly duplicateGroupId?: string | null;
  readonly isDuplicatePrimary?: boolean | null;
  readonly emailThreadId?: string | null;
}

export function duplicateGroupOf(hit: SearchHit): string | null {
  return (hit as RelationshipHit).duplicateGroupId ?? null;
}

export function emailThreadOf(hit: SearchHit): string | null {
  return (hit as RelationshipHit).emailThreadId ?? null;
}

/** The hit is an attachment (a family member below the parent). */
export function isAttachment(hit: SearchHit): boolean {
  return !!hit.parentDocumentId || Number(hit.familySequence ?? 0) > 0;
}

/** A document row of the list. In family groups (E16-T10) it has a tree level and its family's state. */
export interface DocumentRow {
  readonly kind: 'document';
  readonly hit: SearchHit;
  /** Index of the hit among the loaded rows (`ResultWindow.rows`). */
  readonly index: number;
  /** 1 for a parent or stand-alone document, 2 for an attachment shown under its parent. */
  readonly level: 1 | 2;
  /** Family key (family id, else the document id). */
  readonly family: string;
  /** A level-1 row with attachments in the list: it can be collapsed (`aria-expanded`). */
  readonly hasChildren: boolean;
  readonly expanded: boolean;
  /** The family started on an earlier page: the first row after a page boundary says "continued". */
  readonly continued: boolean;
  /** The family goes on past the loaded rows, on the next page. */
  readonly continues: boolean;
}

/**
 * A row standing in for a parent that is not in the list: `orphan` when the parent is not among the results (or not
 * visible to the reviewer), `continued` when the family started on the previous page, before the loaded rows.
 */
export interface PlaceholderRow {
  readonly kind: 'placeholder';
  readonly reason: 'orphan' | 'continued';
  readonly level: 1;
  readonly family: string;
  readonly hasChildren: true;
  readonly expanded: boolean;
  /** Index (in the loaded rows) of the first attachment it stands above. */
  readonly index: number;
}

export type DisplayRow = DocumentRow | PlaceholderRow;

export interface GroupOptions {
  /** Indexes (in the loaded rows) where a new server page starts, after the first. */
  readonly pageStarts: readonly number[];
  /** Pages before the loaded ones exist. */
  readonly hasPrevious: boolean;
  /** Pages after the loaded ones exist. */
  readonly hasNext: boolean;
  /** Collapsed families (keys). */
  readonly collapsed: ReadonlySet<string>;
}

/** The plain list: one level-1 row per hit. */
export function flatRows(rows: readonly SearchHit[]): DisplayRow[] {
  return rows.map((hit, index) => ({
    kind: 'document',
    hit,
    index,
    level: 1,
    family: familyKey(hit),
    hasChildren: false,
    expanded: false,
    continued: false,
    continues: false,
  }));
}

export function familyKey(hit: SearchHit): string {
  return hit.familyId ?? hit.documentId;
}

/**
 * Family groups of a list sorted by Family Date (which keeps each family contiguous, parent first, then attachments by
 * Family Sequence): every parent with its attachments indented below it, collapsible. A family never crosses a page
 * boundary silently: the first row after the boundary is marked "continued", and the last loaded row says the family
 * continues on the next page. Attachments whose parent is not in the list get a parent placeholder. Rows of collapsed
 * families are left out.
 */
export function groupFamilies(rows: readonly SearchHit[], options: GroupOptions): DisplayRow[] {
  const out: DisplayRow[] = [];
  const pageStarts = new Set(options.pageStarts);
  let i = 0;
  while (i < rows.length) {
    const key = familyKey(rows[i]);
    let end = i + 1;
    while (end < rows.length && familyKey(rows[end]) === key) end++;
    const group = rows.slice(i, end);
    const expanded = !options.collapsed.has(key);
    const atEnd = end === rows.length && options.hasNext;
    const first = group[0];
    const headIsChild = isAttachment(first);
    const familyMayContinue = (hit: SearchHit) => isAttachment(hit) || !!hit.isFamilyParent;
    let childStart = i + 1;
    if (headIsChild) {
      out.push({
        kind: 'placeholder',
        reason: i === 0 && options.hasPrevious ? 'continued' : 'orphan',
        level: 1,
        family: key,
        hasChildren: true,
        expanded,
        index: i,
      });
      childStart = i;
    } else {
      out.push({
        kind: 'document',
        hit: first,
        index: i,
        level: 1,
        family: key,
        hasChildren: group.length > 1 || (atEnd && !!first.isFamilyParent),
        expanded,
        continued: false,
        // A collapsed family says it on its parent row.
        continues: atEnd && (group.length === 1 ? familyMayContinue(first) : !expanded),
      });
    }
    if (expanded) {
      for (let j = childStart; j < end; j++) {
        out.push({
          kind: 'document',
          hit: rows[j],
          index: j,
          level: 2,
          family: key,
          hasChildren: false,
          expanded: false,
          continued: pageStarts.has(j) && j > i,
          continues: atEnd && j === end - 1,
        });
      }
    }
    i = end;
  }
  return out;
}

/** Display index of each loaded row (-1 while its family is collapsed). */
export function displayIndexes(display: readonly DisplayRow[], rowCount: number): Int32Array {
  const map = new Int32Array(rowCount).fill(-1);
  display.forEach((row, d) => {
    if (row.kind === 'document') map[row.index] = d;
  });
  return map;
}
