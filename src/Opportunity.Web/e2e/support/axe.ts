import { readFileSync } from 'node:fs';
import { expect, type Page, type TestInfo } from '@playwright/test';
import type { AxeResults, Result, RunOptions } from 'axe-core';

// axe-core (already a devDependency for the unit specs) is evaluated through the DevTools protocol, which the page's
// CSP does not apply to, so the tests keep the production CSP instead of bypassing it.
const axeSource = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

const options: RunOptions = {
  runOnly: {
    type: 'tag',
    values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'],
  },
  resultTypes: ['violations'],
};

/** Impacts that fail CI (ADR-018 §10.3); moderate and minor findings are attached to the report only. */
export const BLOCKING_IMPACTS: readonly string[] = ['serious', 'critical'];

function describe(violations: Result[]): string {
  return violations
    .map(
      (v) =>
        `${v.id} (${v.impact}): ${v.help} — ${v.helpUrl}\n` +
        v.nodes.map((n) => `    ${n.target.join(' ')}\n      ${n.failureSummary ?? ''}`).join('\n'),
    )
    .join('\n');
}

/** Runs axe (WCAG 2.0–2.2 A/AA + best practice, colour contrast included) on the whole page. */
export async function expectNoSeriousAxeViolations(page: Page, testInfo: TestInfo): Promise<void> {
  await page.evaluate(axeSource);
  const results = await page.evaluate(
    (opts) =>
      (
        window as unknown as { axe: { run: (c: Document, o: unknown) => Promise<AxeResults> } }
      ).axe.run(document, opts),
    options,
  );
  const blocking = results.violations.filter((v) => BLOCKING_IMPACTS.includes(v.impact ?? ''));
  const other = results.violations.filter((v) => !BLOCKING_IMPACTS.includes(v.impact ?? ''));
  if (other.length) {
    testInfo.annotations.push({ type: 'axe (non-blocking)', description: describe(other) });
  }
  expect(
    blocking,
    `Serious/critical axe violations on ${page.url()}:\n${describe(blocking)}`,
  ).toEqual([]);
}
