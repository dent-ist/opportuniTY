import { DisplayRow, displayIndexes, flatRows, groupFamilies } from './family-groups';
import { hit } from './grid-fixtures.testing';

/** Family `f` parent `n` (isFamilyParent) or attachment `n` of parent `p`. */
const parent = (n: number) => hit(n, { familyId: `fam-${n}`, isFamilyParent: true });
const child = (n: number, p: number, seq = 1) =>
  hit(n, { familyId: `fam-${p}`, parentDocumentId: `doc-${p}`, familySequence: seq });
const single = (n: number) => hit(n);

const none = { pageStarts: [], hasPrevious: false, hasNext: false, collapsed: new Set<string>() };

/** "L1 ACM0000001", "L2 ACM0000002", "orphan", "continued" (+ markers). */
function describeRows(rows: readonly DisplayRow[]): string[] {
  return rows.map((r) => {
    if (r.kind === 'placeholder') return `${r.reason}${r.expanded ? '' : ' (collapsed)'}`;
    const marks = [
      r.hasChildren ? (r.expanded ? 'open' : 'closed') : '',
      r.continued ? 'continued' : '',
      r.continues ? 'continues' : '',
    ].filter(Boolean);
    return `L${r.level} ${r.hit.controlNumber.slice(-2)}${marks.length ? ` [${marks.join(',')}]` : ''}`;
  });
}

describe('Family groups (E16-T10)', () => {
  it('shows each parent with its attachments indented below it, stand-alone documents at the top level', () => {
    const rows = [single(1), parent(2), child(3, 2, 1), child(4, 2, 2), single(5)];
    expect(describeRows(groupFamilies(rows, none))).toEqual([
      'L1 01',
      'L1 02 [open]',
      'L2 03',
      'L2 04',
      'L1 05',
    ]);
  });

  it('puts a parent placeholder above attachments whose parent is not in the list', () => {
    const rows = [single(1), child(7, 6), child(8, 6, 2), single(9)];
    const display = groupFamilies(rows, none);
    expect(describeRows(display)).toEqual(['L1 01', 'orphan', 'L2 07', 'L2 08', 'L1 09']);
    expect(display[1]).toMatchObject({ kind: 'placeholder', index: 1, hasChildren: true });
  });

  it('marks a family split across a page boundary as continued, and the family going on to the next page', () => {
    // Page 1: rows 0–2, page 2: rows 3–4; the family of 3 starts on page 1 and ends on page 2.
    const rows = [single(1), parent(3), child(4, 3, 1), child(5, 3, 2), parent(6)];
    const display = groupFamilies(rows, { ...none, pageStarts: [3], hasNext: true });
    expect(describeRows(display)).toEqual([
      'L1 01',
      'L1 03 [open]',
      'L2 04',
      'L2 05 [continued]',
      // Its attachments are on the next page: the parent says so.
      'L1 06 [open,continues]',
    ]);
  });

  it('starts with a "continued" placeholder when the loaded rows begin inside a family', () => {
    const rows = [child(4, 3, 1), child(5, 3, 2), single(6)];
    const display = groupFamilies(rows, { ...none, hasPrevious: true });
    expect(describeRows(display)).toEqual(['continued', 'L2 04', 'L2 05', 'L1 06']);
  });

  it('says an attachment at the end of the loaded rows may continue on the next page', () => {
    const rows = [parent(3), child(4, 3)];
    expect(describeRows(groupFamilies(rows, { ...none, hasNext: true }))).toEqual([
      'L1 03 [open]',
      'L2 04 [continues]',
    ]);
    expect(describeRows(groupFamilies(rows, none))).toEqual(['L1 03 [open]', 'L2 04']);
  });

  it('leaves the attachments of a collapsed family out, and maps their rows to the family row', () => {
    const rows = [single(1), parent(2), child(3, 2), child(4, 2, 2), child(7, 6)];
    const collapsed = new Set(['fam-2', 'fam-6']);
    const display = groupFamilies(rows, { ...none, collapsed, hasNext: true });
    expect(describeRows(display)).toEqual(['L1 01', 'L1 02 [closed]', 'orphan (collapsed)']);
    expect([...displayIndexes(display, rows.length)]).toEqual([0, 1, -1, -1, -1]);
  });

  it('keeps the plain list flat', () => {
    const rows = [parent(2), child(3, 2)];
    expect(describeRows(flatRows(rows))).toEqual(['L1 02', 'L1 03']);
  });
});
