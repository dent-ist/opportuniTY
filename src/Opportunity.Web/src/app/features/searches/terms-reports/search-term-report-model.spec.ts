import { ApiError } from '../../../core/api/problem-details';
import { type ReportTerm, toReport } from './search-term-report-api';
import {
  canDelete,
  canSeeGenerations,
  errorParts,
  nextSort,
  parsePastedTerms,
  parseTermsCsv,
  reportErrorText,
  scopeLabel,
  sortTerms,
  statusPill,
  termErrorText,
  visibilityNotice,
} from './search-term-report-model';

function term(id: string, name: string, hits: number | null, extra: Partial<ReportTerm> = {}) {
  return {
    termId: id,
    name,
    expression: name.toLowerCase(),
    error: null,
    documentsWithHits: hits,
    documentsWithHitsIncludingFamily: hits === null ? null : hits + 1,
    uniqueHits: hits === null ? null : 10 - hits,
    uniqueHitsIncludingFamily: null,
    ...extra,
  } satisfies ReportTerm;
}

describe('Search Terms Report rules (#180)', () => {
  describe('pasted terms', () => {
    it('reads one expression per line, named after itself, skipping blank lines', () => {
      expect(parsePastedTerms('terminat*\n\n  "notice period" W/5 days  \r\n')).toEqual({
        terms: [
          { name: 'terminat*', expression: 'terminat*' },
          { name: '"notice period" W/5 days', expression: '"notice period" W/5 days' },
        ],
        issues: [],
        blocking: [],
      });
    });

    it('keeps names of two columns pasted from a spreadsheet (tab-separated)', () => {
      expect(
        parsePastedTerms('Termination\tterminat*\nPenalty\tpenalt* OR liquidated').terms,
      ).toEqual([
        { name: 'Termination', expression: 'terminat*' },
        { name: 'Penalty', expression: 'penalt* OR liquidated' },
      ]);
    });

    it('skips exact repeats and refuses one name for two expressions', () => {
      const parsed = parsePastedTerms('a\nA\tb\nc\nc');
      expect(parsed.terms).toEqual([
        { name: 'a', expression: 'a' },
        { name: 'c', expression: 'c' },
      ]);
      expect(parsed.issues).toEqual([{ line: 4, message: 'Same term as line 3; skipped.' }]);
      expect(parsed.blocking).toEqual([
        '“A” names two different expressions (lines 1 and 2). Give each term its own name.',
      ]);
    });
  });

  describe('CSV upload', () => {
    it('reads Name,Expression rows with an optional header, quoting, BOM and CRLF', () => {
      const csv =
        '﻿Name,Expression\r\nTermination,terminat*\r\n"Price, fixed","price W/3 ""fixed"""\r\nsolo\r\n\r\n';
      expect(parseTermsCsv(csv)).toEqual({
        terms: [
          { name: 'Termination', expression: 'terminat*' },
          { name: 'Price, fixed', expression: 'price W/3 "fixed"' },
          { name: 'solo', expression: 'solo' },
        ],
        issues: [],
        blocking: [],
      });
    });

    it('reports rows it cannot use by line and keeps the others', () => {
      const parsed = parseTermsCsv('A,a\nB,\nC,c,extra\n"D","multi\nline"\nE,e');
      expect(parsed.terms.map((t) => t.name)).toEqual(['A', 'D', 'E']);
      expect(parsed.terms[1].expression).toBe('multi\nline');
      expect(parsed.issues).toEqual([
        { line: 2, message: 'No expression.' },
        {
          line: 3,
          message: 'More than two columns. Put the expression in double quotes when it has commas.',
        },
      ]);
    });
  });

  it('words scopes, statuses and errors plainly', () => {
    expect(scopeLabel({ kind: 'workspace', id: null, name: null })).toBe('Whole workspace');
    expect(scopeLabel({ kind: 'savedSearch', id: 'ss-1', name: 'Hot docs' })).toBe(
      'Saved search “Hot docs”',
    );
    expect(scopeLabel({ kind: 'snapshot', id: 'snap-1', name: null })).toBe('Frozen set “snap-1”');
    expect(statusPill('completed')).toBe('succeeded');
    expect(statusPill('queued')).toBe('queued');
    expect(statusPill('somethingNew')).toBe('running');
    expect(termErrorText({ code: 'x', message: 'Missing closing quote', position: 8 })).toBe(
      'Syntax error at character 9: Missing closing quote.',
    );
    expect(termErrorText({ code: 'x', message: 'Bad.', position: null })).toBe(
      'Syntax error: Bad.',
    );
    expect(errorParts('"breach of', 0)).toEqual({ before: '', at: '"', after: 'breach of' });
    expect(errorParts('(a', 2)).toEqual({ before: '(a', at: '', after: '' });
    expect(errorParts('a', null)).toBeNull();
  });

  it('says whose view of the documents the counts are', () => {
    const me = { userId: 'u-1', displayName: 'Alex Reviewer' };
    expect(visibilityNotice(me, 'u-1')).toContain('documents you could see when you ran');
    expect(visibilityNotice(me, 'u-2')).toContain(
      'only the documents Alex Reviewer could see when they ran this report',
    );
  });

  it('sorts by a column, keeps entered order as the tiebreak and puts terms with errors last', () => {
    const terms = [
      term('1', 'Bravo', 5),
      term('2', 'Alpha', null, {
        error: { code: 'x', message: 'bad', position: 1 },
      }),
      term('3', 'Charlie', 9),
      term('4', 'Delta', 5),
    ];
    const ids = (sort: Parameters<typeof sortTerms>[1]) =>
      sortTerms(terms, sort).map((t) => t.termId);
    expect(ids({ key: 'entered', direction: 'asc' })).toEqual(['1', '3', '4', '2']);
    expect(ids({ key: 'documentsWithHits', direction: 'desc' })).toEqual(['3', '1', '4', '2']);
    expect(ids({ key: 'documentsWithHits', direction: 'asc' })).toEqual(['1', '4', '3', '2']);
    expect(ids({ key: 'name', direction: 'asc' })).toEqual(['1', '3', '4', '2']);
    expect(ids({ key: 'uniqueHits', direction: 'desc' })).toEqual(['1', '4', '3', '2']);
  });

  it('cycles a header: numbers high to low first, names A to Z, then back to the entered order', () => {
    const entered = { key: 'entered', direction: 'asc' } as const;
    const a = nextSort(entered, 'documentsWithHits');
    expect(a).toEqual({ key: 'documentsWithHits', direction: 'desc' });
    const b = nextSort(a, 'documentsWithHits');
    expect(b).toEqual({ key: 'documentsWithHits', direction: 'asc' });
    expect(nextSort(b, 'documentsWithHits')).toEqual(entered);
    expect(nextSort(b, 'name')).toEqual({ key: 'name', direction: 'asc' });
  });

  it('lets the person who ran it or an admin delete, and shows generations to admins only', () => {
    const report = { createdBy: { userId: 'u-1', displayName: 'A' } };
    const none = () => false;
    expect(canDelete(report, { userId: 'u-1', can: none })).toBe(true);
    expect(canDelete(report, { userId: 'u-2', can: none })).toBe(false);
    expect(canDelete(report, { userId: 'u-2', can: (p) => p === 'Workspace.ManageSecurity' })).toBe(
      true,
    );
    expect(canSeeGenerations({ can: none })).toBe(false);
    expect(canSeeGenerations({ can: (p) => p === 'Job.ViewAll' })).toBe(true);
  });

  it('explains failures without saying whether a report exists', () => {
    expect(reportErrorText(new ApiError(404, { status: 404 }))).toBe(
      'This Search Terms Report is not available. It may have been deleted.',
    );
    expect(
      reportErrorText(
        new ApiError(400, { status: 400, errors: { terms: ['At most 500 terms per report.'] } }),
      ),
    ).toBe('At most 500 terms per report.');
  });

  it('reads the report resource tolerantly (int64 as strings, missing fields)', () => {
    const r = toReport({
      reportId: 'r-1',
      name: 'Key terms',
      status: 'completed',
      scope: { kind: 'snapshot', id: 'snap-1' },
      termCount: '2',
      createdBy: { userId: 'u-1', displayName: 'Alex' },
      createdAt: '2026-10-03T10:40:00Z',
      job: { jobId: 'job-1' },
      searchGeneration: 18432,
      indexCurrent: false,
      totals: { documentsInScope: '250', documentsWithHits: '40' },
      terms: [
        { termId: 't-1', name: 'a', expression: 'a', documentsWithHits: '12' },
        { termId: 't-2', name: 'b', expression: '"b', error: { message: 'No closing quote' } },
      ],
    });
    expect(r.jobId).toBe('job-1');
    expect(r.termCount).toBe(2);
    expect(r.scope).toEqual({ kind: 'snapshot', id: 'snap-1', name: null });
    expect(r.searchGeneration).toBe('18432');
    expect(r.indexCurrent).toBe(false);
    expect(r.totals?.documentsInScope).toBe(250);
    expect(r.totals?.documentsWithoutHits).toBeNull();
    expect(r.terms[0].documentsWithHits).toBe(12);
    expect(r.terms[1].error).toEqual({
      code: 'invalid',
      message: 'No closing quote',
      position: null,
    });
    expect(r.completedAt).toBeNull();
  });
});
