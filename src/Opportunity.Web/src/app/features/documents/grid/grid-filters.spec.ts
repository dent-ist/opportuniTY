import type { FieldResource } from '../../../core/api/generated/models';
import {
  FilterSpec,
  clauseAt,
  compileFilter,
  compileQuery,
  describeFilter,
  escapeTerm,
  filterProblem,
  filterSpec,
  parseSize,
  quotePhrase,
} from './grid-filters';

const CAPS = {
  aggregatable: false,
  exists: true,
  filterable: true,
  fullText: false,
  highlightable: false,
  leadingWildcard: false,
  rangeable: false,
  sortable: true,
  wildcard: true,
};

function field(
  queryName: string,
  type: FieldResource['type'],
  extra: Partial<FieldResource> = {},
): FieldResource {
  return {
    fieldId: 1,
    queryName,
    displayName: queryName,
    type,
    storage: 'column',
    multiValue: false,
    isSystem: true,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    reducedCapabilities: false,
    capabilities: CAPS,
    choices: null,
    ...extra,
  };
}

function spec(kind: FilterSpec['kind'], extra: Partial<FilterSpec> = {}): FilterSpec {
  return { queryName: 'f', label: 'F', kind, contains: false, choices: [], ...extra };
}

describe('filter row: control per field type', () => {
  it('picks the control from the type and capabilities of the field', () => {
    expect(filterSpec(field('custodian', 'keyword'), 'Custodian', 'text')).toEqual({
      queryName: 'custodian',
      label: 'Custodian',
      kind: 'text',
      contains: false,
      choices: [],
    });
    const name = field('filename', 'text', {
      capabilities: { ...CAPS, leadingWildcard: true },
    });
    expect(filterSpec(name, 'File Name', 'text')?.contains).toBe(true);
    expect(filterSpec(field('date', 'date'), 'Date', 'date')?.kind).toBe('date');
    expect(filterSpec(field('pagecount', 'integer'), 'Pages', 'number')?.kind).toBe('number');
    expect(filterSpec(field('filesize', 'integer'), 'Size', 'size')?.kind).toBe('size');
    expect(filterSpec(field('score', 'decimal'), 'Score', 'number')?.kind).toBe('number');
    expect(filterSpec(field('hot', 'boolean'), 'Hot', 'text')?.kind).toBe('boolean');
    expect(filterSpec(field('reviewer', 'user'), 'Reviewer', 'text')?.kind).toBe('text');
    const choice = filterSpec(
      field('responsiveness', 'singleChoice', {
        choices: [
          { choiceId: 1, name: 'Responsive', isActive: true },
          { choiceId: 2, name: 'Not Responsive', isActive: false },
        ],
      }),
      'Responsiveness',
      'text',
    );
    expect(choice).toMatchObject({ kind: 'choice', choices: ['Responsive', 'Not Responsive'] });
    expect(filterSpec(field('issues', 'multiChoice'), 'Issues', 'text')?.kind).toBe('choice');
  });

  it('gives fields without the filterable capability, and unknown fields, no control', () => {
    const locked = field('md5', 'keyword', { capabilities: { ...CAPS, filterable: false } });
    expect(filterSpec(locked, 'MD5', 'text')).toBeNull();
    expect(filterSpec(undefined, 'Gone', 'text')).toBeNull();
  });
});

describe('filter row: compilation into the query language (ADR-008)', () => {
  it('escapes every reserved character, whitespace, wildcards and operators in terms (R1, R4)', () => {
    expect(escapeTerm('AT&T')).toBe('AT&T');
    expect(escapeTerm('a(b)c"d:e[f]g{h}i\\j!k~l^m')).toBe(
      'a\\(b\\)c\\"d\\:e\\[f\\]g\\{h\\}i\\\\j\\!k\\~l\\^m',
    );
    expect(escapeTerm('RE: Q3 terms')).toBe('RE\\:\\ Q3\\ terms');
    expect(escapeTerm('50%*?')).toBe('50%\\*\\?');
    expect(escapeTerm('W/5')).toBe('W\\/5');
    expect(escapeTerm('-draft')).toBe('\\-draft');
    expect(escapeTerm('+1')).toBe('\\+1');
    expect(escapeTerm('e-mail')).toBe('e-mail');
    expect(escapeTerm('OR')).toBe('\\OR');
    expect(escapeTerm('or')).toBe('or');
    expect(quotePhrase('say "hi" \\ bye')).toBe('"say \\"hi\\" \\\\ bye"');
  });

  it('text: starts with, contains where the field takes leading wildcards, quoted = exact (R7)', () => {
    expect(
      compileFilter(spec('text', { queryName: 'custodian' }), { op: 'text', text: 'Smi' }),
    ).toBe('custodian:Smi*');
    expect(
      compileFilter(spec('text', { queryName: 'filename', contains: true }), {
        op: 'text',
        text: ' RE: budget (v2) ',
      }),
    ).toBe('filename:*RE\\:\\ budget\\ \\(v2\\)*');
    expect(
      compileFilter(spec('text', { queryName: 'custodian' }), {
        op: 'text',
        text: '"Jane "JD" Doe"',
      }),
    ).toBe('custodian:"Jane \\"JD\\" Doe"');
    expect(compileFilter(spec('text'), { op: 'text', text: '"' })).toBe('f:\\"*');
    expect(compileFilter(spec('text'), { op: 'text', text: '""' })).toBe('f:\\"\\"*');
    expect(compileFilter(spec('text'), { op: 'text', text: '   ' })).toBeNull();
  });

  it('dates: from/to as an inclusive range with * for an open end (R8, R9)', () => {
    const date = spec('date', { queryName: 'date' });
    expect(compileFilter(date, { op: 'range', from: '2024-01-01', to: '2024-03-31' })).toBe(
      'date:[2024-01-01 TO 2024-03-31]',
    );
    expect(compileFilter(date, { op: 'range', from: '2024-01-01', to: '' })).toBe(
      'date:[2024-01-01 TO *]',
    );
    expect(compileFilter(date, { op: 'range', from: '', to: '2024' })).toBe('date:[* TO 2024]');
    expect(compileFilter(date, { op: 'range', from: '', to: '' })).toBeNull();
    expect(compileFilter(date, { op: 'range', from: '2024-13-40', to: '' })).toBeNull();
    expect(filterProblem(date, { op: 'range', from: '01/02/2024', to: '' })).toBe(
      'Enter dates as YYYY-MM-DD.',
    );
    expect(filterProblem(date, { op: 'range', from: '2024-05-01', to: '2024-04-01' })).toBe(
      '"From" is after "To".',
    );
  });

  it('numbers and file sizes: invariant ranges, sizes in bytes from a unit (R9)', () => {
    const pages = spec('number', { queryName: 'pagecount' });
    expect(compileFilter(pages, { op: 'range', from: '2', to: '10' })).toBe('pagecount:[2 TO 10]');
    expect(compileFilter(pages, { op: 'range', from: '-5', to: '' })).toBe('pagecount:[\\-5 TO *]');
    expect(compileFilter(pages, { op: 'range', from: '1,000', to: '' })).toBeNull();
    expect(filterProblem(pages, { op: 'range', from: '20', to: '3' })).toBe(
      '"From" is after "To".',
    );
    const size = spec('size', { queryName: 'filesize' });
    expect(compileFilter(size, { op: 'range', from: '10 KB', to: '2.5mb' })).toBe(
      'filesize:[10240 TO 2621440]',
    );
    expect(compileFilter(size, { op: 'range', from: '', to: '512' })).toBe('filesize:[* TO 512]');
    expect(parseSize('1 GB')).toBe(1024 ** 3);
    expect(parseSize('ten')).toBeNull();
    expect(filterProblem(size, { op: 'range', from: 'big', to: '' })).toMatch(/sizes in bytes/);
  });

  it('choices: names as phrases, ORed within the field', () => {
    const choice = spec('choice', { queryName: 'responsiveness' });
    expect(compileFilter(choice, { op: 'choices', names: ['Responsive'] })).toBe(
      'responsiveness:"Responsive"',
    );
    expect(
      compileFilter(choice, { op: 'choices', names: ['Responsive', 'Needs "2nd" look'] }),
    ).toBe('responsiveness:("Responsive" OR "Needs \\"2nd\\" look")');
    expect(compileFilter(choice, { op: 'choices', names: [] })).toBeNull();
  });

  it('booleans and, for every type, has a value / is empty', () => {
    expect(
      compileFilter(spec('boolean', { queryName: 'hot' }), { op: 'boolean', value: true }),
    ).toBe('hot:true');
    expect(
      compileFilter(spec('boolean', { queryName: 'hot' }), { op: 'boolean', value: false }),
    ).toBe('hot:false');
    for (const kind of ['text', 'date', 'number', 'size', 'choice', 'boolean'] as const) {
      expect(compileFilter(spec(kind), { op: 'has' })).toBe('f:*');
      expect(compileFilter(spec(kind), { op: 'empty' })).toBe('NOT f:*');
    }
  });

  it('ANDs the filters with each other and with the grouped keyword query, and locates each clause', () => {
    const clauses = [
      { queryName: 'custodian', clause: 'custodian:Smi*' },
      { queryName: 'date', clause: 'date:[2024-01-01 TO *]' },
    ];
    const compiled = compileQuery('merger OR acquisition', clauses);
    expect(compiled.query).toBe(
      '(merger OR acquisition) AND custodian:Smi* AND date:[2024-01-01 TO *]',
    );
    expect(compiled.query.slice(compiled.clauses[1].start, compiled.clauses[1].end)).toBe(
      'date:[2024-01-01 TO *]',
    );
    expect(clauseAt(compiled, compiled.query.indexOf('Smi'))).toBe('custodian');
    expect(clauseAt(compiled, 3)).toBeNull();
    expect(compileQuery('', clauses).query).toBe('custodian:Smi* AND date:[2024-01-01 TO *]');
    expect(compileQuery('  merger ', [])).toEqual({ query: '  merger ', clauses: [] });
  });

  it('describes filters for chips and screen readers', () => {
    expect(describeFilter(spec('text'), { op: 'text', text: 'RE' })).toBe('starts with “RE”');
    expect(describeFilter(spec('text', { contains: true }), { op: 'text', text: 'q' })).toBe(
      'contains “q”',
    );
    expect(describeFilter(spec('text'), { op: 'text', text: '"Jane Doe"' })).toBe('is "Jane Doe"');
    expect(describeFilter(spec('date'), { op: 'range', from: '2024-01-01', to: '' })).toBe(
      'from 2024-01-01',
    );
    expect(describeFilter(spec('size'), { op: 'range', from: '', to: '1 MB' })).toBe('up to 1 MB');
    expect(describeFilter(spec('choice'), { op: 'choices', names: ['A', 'B'] })).toBe('A or B');
    expect(describeFilter(spec('boolean'), { op: 'empty' })).toBe('is empty');
  });
});
