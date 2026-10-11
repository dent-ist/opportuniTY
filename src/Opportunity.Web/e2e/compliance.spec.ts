import type { Theme } from '../src/app/core/preferences/ui-preferences';
import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { tabTo } from './support/tab';

// Admin › Compliance (E14-T06) against the mock (e2e/support/mock-compliance.ts): the ethical wall proof and the access
// review are generated with the keyboard only, the downloads point at the gateway, and the generated reports pass axe
// in every theme.

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

test('generates the wall proof and the access review with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/compliance');
  await expect(page.getByRole('heading', { level: 1, name: 'Compliance' })).toBeVisible();
  // Nothing is generated (and audited) until asked for.
  expect(mock.compliance.requests).toEqual([]);

  const proof = page.getByRole('region', { name: 'Ethical wall proof' });
  await tabTo(page, proof.getByRole('button', { name: 'Generate proof' }), 40);
  await page.keyboard.press('Enter');
  await expect(proof.getByText('Walls did not hold')).toBeVisible();
  expect(mock.compliance.requests[0].query).toMatch(/^\?from=.+&to=.+$/);
  const attempts = proof.getByRole('table', { name: /Attempts on walled documents/ });
  await expect(attempts.getByRole('row').nth(1)).toContainText('Allowed');
  await expect(attempts.getByRole('row').nth(1)).toContainText('Morgan Walled');
  await expect(proof.getByRole('table', { name: 'Ethical walls' })).toContainText(
    'Group Northwind team',
  );
  await expect(proof.getByText('WallChanged by Dana Counsel')).toBeVisible();
  await expect(proof.getByRole('link', { name: 'Download proof (CSV)' })).toHaveAttribute(
    'href',
    /^\/api\/v1\/workspaces\/ws-1\/compliance\/wall-proof\/export\?from=.+&to=.+$/,
  );

  const review = page.getByRole('region', { name: 'Access review' });
  await tabTo(page, review.getByRole('button', { name: 'Generate review' }), 40);
  await page.keyboard.press('Enter');
  const table = review.getByRole('table', { name: 'Access review' });
  await expect(table.getByRole('row', { name: /Casey Grouped/ })).toContainText(
    'Reviewer (via Northwind team)',
  );
  await expect(table.getByRole('row', { name: /Northwind team/ }).last()).toContainText(
    '1 known member',
  );
  await expect(review.getByRole('link', { name: 'Download review (CSV)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/compliance/access-review/export',
  );
  await expect(page.getByRole('link', { name: 'Download audit export' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/compliance/audit-export',
  );
});

test('a reversed period cannot be generated', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/admin/compliance');
  const proof = page.getByRole('region', { name: 'Ethical wall proof' });
  await proof.getByLabel('From').fill('2026-10-10');
  await proof.getByLabel('To (inclusive)').fill('2026-10-01');
  await expect(proof.getByRole('alert')).toHaveText(
    'The start date must be on or before the end date.',
  );
  await expect(proof.getByRole('button', { name: 'Generate proof' })).toBeDisabled();
  expect(mock.compliance.requests).toEqual([]);
});

for (const theme of THEMES) {
  test(`generated reports pass axe, ${theme} theme`, async ({ page }, testInfo) => {
    await useTheme(page, theme);
    await openPage(page, '/w/ws-1/admin/compliance');
    await page.getByRole('button', { name: 'Generate proof' }).click();
    await page.getByRole('button', { name: 'Generate review' }).click();
    await expect(page.getByRole('table', { name: 'Access review' })).toBeVisible();
    await expect(page.getByText('Walls did not hold')).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
  });
}
