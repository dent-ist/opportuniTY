import type { ImportReportResource } from '../../core/api/generated/models';
import { elapsedText, issueRows, reportGroups } from './import-report';

function summary(overrides: Partial<ImportReportResource> = {}): ImportReportResource {
  return {
    importId: 'imp-1',
    name: 'VOL001.dat 2026-10-04',
    mode: 'append',
    sourceFileName: 'VOL001.dat',
    status: 'completedWithErrors',
    final: true,
    startedAt: '2026-10-04T09:00:00Z',
    completedAt: '2026-10-04T09:02:05Z',
    elapsedSeconds: 125,
    rows: { read: 20, imported: '18', overlaid: 0, skipped: 0, errored: 2, withWarnings: 1 },
    natives: { linked: 17, missing: 1 },
    text: { linked: 18, missing: 0, truncated: 0 },
    images: { documentsLinked: 0, documentsWithoutImages: 0, pagesLinked: 0, pagesMissing: 0 },
    families: { built: 3, orphans: 1 },
    fieldsCreated: 2,
    choicesCreated: 5,
    errorFileRows: 2,
    issueCounts: [
      { code: 'NATIVE_MISSING', severity: 'warning', count: 1 },
      { code: 'DATE_UNPARSEABLE', severity: 'error', count: '2' },
    ],
    ...overrides,
  };
}

describe('import report summary (Q-71)', () => {
  it('groups the report as Rows · Natives and text · Images · Families · Fields', () => {
    const groups = reportGroups(summary());
    expect(groups.map((g) => g.title)).toEqual([
      'Rows',
      'Natives and text',
      'Images',
      'Families',
      'Fields',
    ]);
    expect(groups[0].facts.map((f) => [f.label, f.value])).toEqual([
      ['Rows read from the load file', 20],
      ['New documents loaded', 18],
      ['Existing documents updated', 0],
      ['Rows left unchanged', 0],
      ['Rows not loaded (errors)', 2],
      ['Rows loaded with warnings', 1],
    ]);
  });

  it('flags only non-zero problem figures for attention', () => {
    const flagged = reportGroups(summary()).flatMap((g) =>
      g.facts.filter((f) => f.attention).map((f) => f.label),
    );
    expect(flagged).toEqual([
      'Rows not loaded (errors)',
      'Rows loaded with warnings',
      'Natives missing',
      'Attachments without their parent',
    ]);
  });

  it('says so in words when a group has nothing to report', () => {
    const groups = reportGroups(
      summary({ families: { built: 0, orphans: 0 }, fieldsCreated: 0, choicesCreated: 0 }),
    );
    const empty = (id: string) => groups.find((g) => g.id === id)?.empty;
    expect(empty('images')).toBe('No page images were loaded by this import.');
    expect(empty('families')).toBe('No document families were built by this import.');
    expect(empty('fields')).toBe('No new fields or choices were needed.');
    expect(empty('rows')).toBeNull();
    expect(empty('files')).toBeNull();
  });

  it('keeps an unknown row count unknown', () => {
    const rows = reportGroups(summary({ rows: { ...summary().rows, read: null } }))[0].facts;
    expect(rows[0].value).toBeNull();
  });

  it('describes the elapsed time in words', () => {
    expect(elapsedText(0)).toBe('Took 0 seconds');
    expect(elapsedText(1)).toBe('Took 1 second');
    expect(elapsedText(45.4)).toBe('Took 45 seconds');
    expect(elapsedText(60)).toBe('Took 1 minute');
    expect(elapsedText('125')).toBe('Took 2 minutes 5 seconds');
    expect(elapsedText(3600)).toBe('Took 1 hour');
    expect(elapsedText(7800)).toBe('Took 2 hours 10 minutes');
    expect(elapsedText(null)).toBeNull();
    expect(elapsedText(-1)).toBeNull();
  });

  it('lists issues errors first, then by count', () => {
    expect(issueRows(summary())).toEqual([
      { code: 'DATE_UNPARSEABLE', severity: 'Error', count: 2 },
      { code: 'NATIVE_MISSING', severity: 'Warning', count: 1 },
    ]);
  });
});
