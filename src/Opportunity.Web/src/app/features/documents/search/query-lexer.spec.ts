import { GoldenAstNode, GoldenCase, goldenCases } from './golden.testing';
import { QueryToken, QueryTokenKind, tokenize } from './query-lexer';

const kinds = (text: string) => tokenize(text).map((t) => [t.kind, text.slice(t.start, t.end)]);

describe('query lexer', () => {
  it('classifies operators, fields, phrases, wildcards, ranges and proximity', () => {
    expect(kinds('(contract OR agreement) AND NOT draft')).toEqual([
      ['paren', '('],
      ['term', 'contract'],
      ['operator', 'OR'],
      ['term', 'agreement'],
      ['paren', ')'],
      ['operator', 'AND'],
      ['operator', 'NOT'],
      ['term', 'draft'],
    ]);
    expect(kinds('"trade secret" w/5 misappropriat*')).toEqual([
      ['phrase', '"trade secret"'],
      ['proximity', 'w/5'],
      ['wildcard', 'misappropriat*'],
    ]);
    expect(kinds('amount:{100 TO *] privilege:*')).toEqual([
      ['field', 'amount:'],
      ['range-bracket', '{'],
      ['range-bound', '100'],
      ['range-to', 'TO'],
      ['range-bound', '*'],
      ['range-bracket', ']'],
      ['field', 'privilege:'],
      ['exists', '*'],
    ]);
  });

  it('treats lower-case operators and escaped text as words', () => {
    expect(kinds('this and W\\/5 AT\\&T a\\*b*')).toEqual([
      ['term', 'this'],
      ['term', 'and'],
      ['term', 'W\\/5'],
      ['term', 'AT\\&T'],
      ['wildcard', 'a\\*b*'],
    ]);
  });

  it('never throws: rejected syntax becomes invalid tokens', () => {
    expect(kinds('-draft apple~2 PRE/3 "open')).toEqual([
      ['invalid', '-'],
      ['term', 'draft'],
      ['term', 'apple'],
      ['invalid', '~'],
      ['term', '2'],
      ['invalid', 'PRE/3'],
      ['phrase', '"open'],
    ]);
    expect(tokenize('"open').at(-1)?.unterminated).toBe(true);
    expect(() => tokenize('\\')).not.toThrow();
  });

  describe('agrees with the server parser golden cases (ADR-008 R18)', () => {
    const cases = goldenCases();

    it('reads the golden cases, including every §9 example', () => {
      expect(cases.length).toBeGreaterThan(60);
      expect(cases.filter((c) => c.name.startsWith('s9-')).map((c) => c.query)).toEqual(
        expect.arrayContaining([
          'contract AND termination',
          '"trade secret"',
          'apple W/10 iphone',
          'custodian:"John Smith"',
          'date:[2025-01-01 TO 2025-12-31]',
          'filename:*.xlsx',
        ]),
      );
    });

    for (const c of cases.filter((g) => g.ast)) {
      it(`${c.name}: ${c.description}`, () => {
        const tokens = tokenize(c.query);
        expect(tokens.filter((t) => t.kind === 'invalid')).toEqual([]);
        for (const problem of checkNode(c, c.ast!, tokens)) expect.fail(problem);
      });
    }

    for (const c of cases.filter((g) => isLexicalError(g))) {
      it(`${c.name}: marks ${c.error!.code} where the server does`, () => {
        const token = tokenize(c.query).find(
          (t) => t.start === c.error!.span.start && t.end === c.error!.span.end,
        );
        expect(token?.kind).toBe(c.error!.code === 'UNTERMINATED_PHRASE' ? 'phrase' : 'invalid');
      });
    }
  });
});

const LEXICAL =
  /^(r4-(bang|caret|tilde|pre|ws|wp)-unsupported|r4-leading-(minus|plus)|error-unterminated-phrase)$/;

function isLexicalError(c: GoldenCase): boolean {
  return !!c.error && LEXICAL.test(c.name);
}

/** The client tokens that the AST node's span implies; returns readable mismatches. */
function checkNode(c: GoldenCase, node: GoldenAstNode, tokens: readonly QueryToken[]): string[] {
  const at = (start: number, end?: number) =>
    tokens.find((t) => t.start === start && (end === undefined || t.end === end));
  const expectKind = (kind: QueryTokenKind, start: number, end?: number): string[] => {
    const token = at(start, end);
    return token?.kind === kind
      ? []
      : [
          `expected ${kind} at ${start}${end === undefined ? '' : `–${end}`}, got ${token?.kind ?? 'nothing'}`,
        ];
  };
  const { start, end } = node.span;
  const problems: string[] = [];
  switch (node.kind) {
    case 'term':
    case 'phrase':
    case 'wildcard':
    case 'exists':
      problems.push(...expectKind(node.kind, start, end));
      break;
    case 'field':
      problems.push(...expectKind('field', start));
      break;
    case 'range':
      problems.push(...expectKind('range-bracket', start, start + 1));
      problems.push(...expectKind('range-bracket', end - 1, end));
      break;
    case 'not':
      if (c.query.startsWith('NOT', start)) problems.push(...expectKind('operator', start));
      break;
    case 'proximity': {
      const between = tokens.filter(
        (t) => t.start >= node.left!.span.end && t.end <= node.right!.span.start,
      );
      if (!between.some((t) => t.kind === 'proximity'))
        problems.push(`no proximity token in ${start}–${end}`);
      break;
    }
    case 'and':
    case 'or': {
      const children = node.children ?? [];
      for (let i = 1; i < children.length; i++) {
        const gap = tokens.filter(
          (t) => t.start >= children[i - 1].span.end && t.end <= children[i].span.start,
        );
        for (const t of gap) {
          if (t.kind !== 'operator' && t.kind !== 'paren')
            problems.push(`${t.kind} between operands`);
        }
      }
      break;
    }
  }
  for (const child of [node.child, node.left, node.right, ...(node.children ?? [])]) {
    if (child) problems.push(...checkNode(c, child, tokens));
  }
  return problems;
}
