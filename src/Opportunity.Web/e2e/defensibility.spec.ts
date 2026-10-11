import type { Theme } from '../src/app/core/preferences/ui-preferences';
import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { DefensibilityMock } from './support/mock-defensibility';
import { tabTo } from './support/tab';

// Admin › Defensibility Reports (E14-T05) against the mock (e2e/support/mock-defensibility.ts): reports are generated
// with the keyboard only and state their point and fingerprint, the hit reports note the index lag at execution, a
// document's chain of custody shows the import hash next to the hash read back from storage, the downloads point at the
// gateway, and the generated reports pass axe in every theme.

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];
const WS = '/api/v1/workspaces/ws-1';

test('generates the reports and traces a document with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/defensibility');
  await expect(
    page.getByRole('heading', { level: 1, name: 'Defensibility Reports' }),
  ).toBeVisible();
  // Nothing is generated (and audited) until asked for.
  expect(mock.defensibility.requests).toEqual([]);

  const review = page.getByRole('region', { name: 'Review population and coding' });
  await tabTo(page, review.getByRole('button', { name: 'Generate summary' }), 40);
  await page.keyboard.press('Enter');
  await expect(review.getByText('830 reviewed')).toBeVisible();
  await expect(
    review.getByText('3f9a1c0e7b52d8a4e6f1093c2b7d5e8a1f4c6b9d0e2a7f3c5b8d1e4a6c9f0b2d'),
  ).toBeVisible();
  await expect(
    review.getByRole('table', { name: 'Coding by reviewer', exact: true }),
  ).toContainText('Riley Reviewer');

  const hits = page.getByRole('region', { name: 'Search term hit reports' });
  await tabTo(page, hits.getByRole('button', { name: 'Generate hit reports' }), 40);
  await page.keyboard.press('Enter');
  await expect(hits.getByRole('table', { name: 'Search term hit reports' })).toContainText(
    '3 changes not yet searchable, oldest 4.5 s',
  );

  const custody = page.getByRole('region', { name: 'Chain of custody' });
  await tabTo(page, custody.getByLabel('Document ID'), 40);
  await page.keyboard.type(DefensibilityMock.documentId);
  await page.keyboard.press('Enter');
  await expect(custody.getByText('Files do not match')).toBeVisible();
  await expect(
    custody
      .getByRole('table', { name: /Stored files of the document/ })
      .getByRole('row', { name: /Native/ }),
  ).toContainText('Does not match');
  await expect(
    custody.getByRole('link', { name: 'Download chain of custody (CSV)' }),
  ).toHaveAttribute(
    'href',
    `${WS}/documents/${DefensibilityMock.documentId}/chain-of-custody/export`,
  );

  await expect(page.getByRole('link', { name: 'Download report pack' })).toHaveAttribute(
    'href',
    `${WS}/defensibility/pack`,
  );
  expect(mock.defensibility.requests.map((r) => r.path)).toEqual([
    `${WS}/defensibility/review-summary`,
    `${WS}/defensibility/search-term-hits`,
    `${WS}/documents/${DefensibilityMock.documentId}/chain-of-custody`,
  ]);
});

test('a report point in the future cannot be generated', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/admin/defensibility');
  await page.getByLabel('As of (optional)').fill('2999-01-01T00:00');
  await expect(page.getByText('The point cannot be in the future.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Generate history' })).toBeDisabled();
  expect(mock.defensibility.requests).toEqual([]);
});

for (const theme of THEMES) {
  test(`generated reports pass axe, ${theme} theme`, async ({ page }, testInfo) => {
    await useTheme(page, theme);
    await openPage(page, '/w/ws-1/admin/defensibility');
    await page.getByRole('button', { name: 'Generate summary' }).click();
    await page.getByRole('button', { name: 'Generate history' }).click();
    await page.getByRole('button', { name: 'Generate hit reports' }).click();
    await page.getByLabel('Document ID').fill(DefensibilityMock.documentId);
    await page.getByRole('button', { name: 'Trace document' }).click();
    await expect(page.getByRole('table', { name: 'Productions' })).toBeVisible();
    await expect(page.getByText('Files do not match')).toBeVisible();
    await expect(
      page.getByRole('table', { name: 'Coding by reviewer', exact: true }),
    ).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
  });
}
