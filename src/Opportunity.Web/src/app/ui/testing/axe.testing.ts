import axe from 'axe-core';

/**
 * Runs axe-core (WCAG 2.0–2.2 A/AA rules) against a rendered element and fails with a readable list.
 * Colour contrast is skipped here because jsdom does not compute styles; tokens.spec.ts checks every
 * colour pair of every theme instead. Test-only: axe-core (MPL-2.0) is a devDependency and never shipped.
 */
export async function expectNoAxeViolations(element: Element): Promise<void> {
  const results = await axe.run(element, {
    runOnly: {
      type: 'tag',
      values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'],
    },
    rules: {
      'color-contrast': { enabled: false },
      // Fragments are tested in isolation; landmarks are checked on the showcase page.
      region: { enabled: false },
      'page-has-heading-one': { enabled: false },
      'landmark-one-main': { enabled: false },
    },
    resultTypes: ['violations'],
  });
  const violations = results.violations.map(
    (v) =>
      `${v.id} (${v.impact}): ${v.help}\n  ${v.nodes.map((n) => n.target.join(' ')).join('\n  ')}`,
  );
  expect(violations, violations.join('\n')).toEqual([]);
}
