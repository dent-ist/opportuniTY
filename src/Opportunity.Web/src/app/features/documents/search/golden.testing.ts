import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import type { QueryValidationResult } from '../../../core/api/generated/models';

/**
 * The server parser's golden cases (ADR-008 R18, tests/Opportunity.UnitTests/QueryLanguage/Golden): the
 * query bar is checked against the same contract the parser is, so a grammar change that the client lexer
 * does not follow fails here. The files are a small fixed YAML subset, read without a YAML dependency.
 */
export interface GoldenSpan {
  start: number;
  end: number;
}

export interface GoldenAstNode {
  kind: string;
  span: GoldenSpan;
  name?: string;
  child?: GoldenAstNode;
  children?: GoldenAstNode[];
  left?: GoldenAstNode;
  right?: GoldenAstNode;
}

export interface GoldenCase {
  name: string;
  description: string;
  query: string;
  printed?: string;
  ast?: GoldenAstNode;
  warnings: { code: string; span: GoldenSpan }[];
  error?: { code: string; span: GoldenSpan; expected: string[]; message: string };
}

const DIRECTORY = '../../tests/Opportunity.UnitTests/QueryLanguage/Golden';

export function goldenCases(): GoldenCase[] {
  return readdirSync(DIRECTORY)
    .filter((f) => f.endsWith('.yaml'))
    .sort()
    .map((f) => parse(f.replace(/\.yaml$/, ''), readFileSync(join(DIRECTORY, f), 'utf8')));
}

function parse(name: string, yaml: string): GoldenCase {
  const lines = yaml.replace(/\r/g, '').split('\n');
  const scalar = (key: string, indent = '') => {
    const line = lines.find((l) => l.startsWith(`${indent}${key}: `));
    return line === undefined
      ? undefined
      : (JSON.parse(line.slice(indent.length + key.length + 2)) as unknown);
  };
  const span = (value: unknown): GoldenSpan => {
    const [start, end] = value as [number, number];
    return { start, end };
  };

  const astAt = lines.indexOf('ast: |');
  let ast: GoldenAstNode | undefined;
  if (astAt >= 0) {
    // The block ends at the next top-level key (e.g. the planner's expected OpenSearch query).
    const rest = lines.slice(astAt + 1);
    const end = rest.findIndex((l) => l !== '' && !l.startsWith('  '));
    const body = end < 0 ? rest : rest.slice(0, end);
    ast = (JSON.parse(body.join('\n')) as { root: GoldenAstNode }).root;
  }

  const warnings: GoldenCase['warnings'] = [];
  lines.forEach((line, i) => {
    if (line.startsWith('  - code: ')) {
      warnings.push({ code: line.slice(10), span: span(JSON.parse(lines[i + 1].slice(10))) });
    }
  });

  const hasError = lines.includes('error:');
  return {
    name,
    description: scalar('description') as string,
    query: scalar('query') as string,
    printed: scalar('printed') as string | undefined,
    ast,
    warnings,
    error: hasError
      ? {
          code: lines.find((l) => l.startsWith('  code: '))!.slice(8),
          span: span(scalar('span', '  ')),
          expected: (scalar('expected', '  ') as string[] | undefined) ?? [],
          message: scalar('message', '  ') as string,
        }
      : undefined,
  };
}

/** What `POST …/query-validations` answers for a golden case. */
export function goldenResult(c: GoldenCase): QueryValidationResult {
  if (c.error) {
    return {
      valid: false,
      astVersion: 1,
      errors: [
        {
          code: c.error.code,
          message: c.error.message,
          span: c.error.span,
          expected: c.error.expected,
        },
      ],
      warnings: [],
    };
  }
  return {
    valid: true,
    astVersion: 1,
    normalized: c.printed ?? '',
    errors: [],
    warnings: c.warnings.map((w) => {
      const word = c.query.slice(w.span.start, w.span.end);
      return {
        code: w.code,
        message: `'${word}' is searched as a word. Did you mean ${word.toUpperCase()}? Operators must be upper case.`,
        span: w.span,
        expected: [],
      };
    }),
  };
}
