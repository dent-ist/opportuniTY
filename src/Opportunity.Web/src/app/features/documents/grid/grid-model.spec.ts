import type { FieldResource, SearchFreshness } from '../../../core/api/generated/models';
import { defaultColumns, familyMarker } from './grid-columns';
import { CellFormatter, countLabel, freshnessLabel } from './grid-format';
import { fakePage, hit } from './grid-fixtures.testing';
import { ResultWindow, toLoadedPage } from './result-window';

const CURRENT: SearchFreshness = {
  asOf: '2026-10-04T10:42:00Z',
  current: true,
  servedGeneration: 7,
  state: 'current',
  indexedThroughGeneration: 7,
  pendingChanges: 0,
  lagSeconds: 0,
};
const UNKNOWN: SearchFreshness = { ...CURRENT, current: null };

describe('Document list counts (Q-10, Q-32)', () => {
  it('shows exact, approximate and capped counts', () => {
    expect(countLabel({ value: 58330, relation: 'eq' }, CURRENT, 'en-US')).toBe('58,330');
    expect(countLabel({ value: 58330, relation: 'eq' }, UNKNOWN, 'en-US')).toBe('≈ 58,330');
    expect(countLabel({ value: 10000, relation: 'gte' }, CURRENT, 'en-US')).toBe(
      '≥ 10,000 (approx.)',
    );
    expect(countLabel({ value: 1_234_567, relation: 'eq' }, UNKNOWN, 'en-US')).toBe('~1.2M');
  });

  it('states freshness in plain language in the workspace time zone', () => {
    expect(freshnessLabel(CURRENT, 'en-GB', 'Europe/Berlin')).toBe('Results current as of 12:42');
    expect(freshnessLabel({ ...CURRENT, current: false }, 'en-GB', 'UTC')).toBe(
      'Results as of 10:42',
    );
    // The wave-10 state wins over the M1 flag.
    const delayed = { ...CURRENT, current: false, state: 'delayed' } as typeof CURRENT;
    expect(freshnessLabel(delayed, 'en-GB', 'UTC')).toBe('Results as of 10:42');
  });

  it('formats cells with the zone shown', () => {
    const f = new CellFormatter('en-GB', 'UTC');
    expect(f.formatDate('2024-03-01T14:30:00Z')).toBe('01/03/2024, 14:30 UTC');
    expect(f.formatSize(512)).toBe('512 B');
    expect(f.formatSize(2048)).toBe('2 KB');
    expect(f.formatSize(5.5 * 1024 * 1024)).toBe('5.5 MB');
  });
});

describe('Document list columns', () => {
  const field = (queryName: string, displayName: string, extra: Partial<FieldResource> = {}) =>
    ({
      queryName,
      displayName,
      isHidden: false,
      capabilities: { sortable: true },
      ...extra,
    }) as FieldResource;

  it('pins Control Number and takes View columns from the field catalogue', () => {
    const { controlNumber, view } = defaultColumns([
      field('controlnumber', 'Control No.'),
      field('date', 'Date'),
      field('filename', 'File Name', { capabilities: { sortable: false } as never }),
      field('filetype', 'File Type', { isHidden: true }),
      field('pagecount', 'Pages'),
    ]);
    expect(controlNumber.label).toBe('Control No.');
    expect(view.map((c) => [c.label, c.sortField])).toEqual([
      ['Date', 'documentDate'],
      ['File Name', null],
      ['Pages', 'pageCount'],
    ]);
  });

  it('falls back to the structural columns without a catalogue', () => {
    expect(defaultColumns(null).view.map((c) => c.queryName)).toEqual([
      'date',
      'familydate',
      'filename',
      'filetype',
      'extension',
      'filesize',
      'pagecount',
    ]);
  });

  it('marks parents and attachments, not standalone documents', () => {
    expect(familyMarker(hit(1))).toBeNull();
    expect(familyMarker(hit(1, { isFamilyParent: false }))).toBeNull();
    expect(familyMarker(hit(1, { isFamilyParent: true }))).toEqual({
      symbol: 'P',
      label: 'Parent',
    });
    expect(
      familyMarker(hit(2, { familyId: 'doc-1', familySequence: 1, parentDocumentId: 'doc-1' })),
    ).toEqual({
      symbol: '└A',
      label: 'Attachment',
    });
  });
});

describe('ResultWindow', () => {
  const options = { total: 250, pageSize: 100 };
  const page = (n: number) => toLoadedPage(fakePage(options, n));

  it('keeps contiguous pages and positions rows by page number', () => {
    let w = ResultWindow.of(page(2), 100);
    w = w.append(page(3)).prepend(page(1));
    expect(w.rows.length).toBe(250);
    expect(w.pages.map((p) => p.number)).toEqual([1, 2, 3]);
    expect(w.pageIndexOf(0)).toBe(0);
    expect(w.pageIndexOf(199)).toBe(1);
    expect(w.pageIndexOf(200)).toBe(2);
    expect(w.startOf(2)).toBe(200);
    expect(w.position(249)).toBe(250);
    expect(w.hasNext).toBe(false);
    expect(w.hasPrevious).toBe(false);
    expect(w.indexOf('doc-101')).toBe(100);
  });

  it('positions from page numbers even when pages were post-filtered short', () => {
    const short = { ...page(1), items: page(1).items.slice(0, 90) };
    const w = ResultWindow.of(short, 100).append(page(2));
    expect(w.position(90)).toBe(101);
  });

  it('has no positions on pages without a number', () => {
    const w = ResultWindow.of({ ...page(3), number: null }, 100);
    expect(w.position(0)).toBeNull();
  });
});
