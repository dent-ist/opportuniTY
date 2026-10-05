import type { Page } from '@playwright/test';
import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Searches › Search Terms Reports (#180) against the wave-11 shared contract served by the mock
// (e2e/support/mock-search-term-reports.ts): create → run → open a term with the keyboard only, the results screen
// (frozen set, visibility, per-term errors, totals, exports, re-run) and the Saved Searches entry point.

/** A notification toast with `text` (the live region repeats it for screen readers). */
const toast = (page: Page, text: string) =>
  page.getByRole('region', { name: 'Notifications' }).getByText(text);

test('creates a report, follows it to completion and opens a term in Documents with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/searches/terms-reports');
  await expect(page.getByRole('heading', { level: 1, name: 'Search Terms Reports' })).toBeVisible();
  const list = page.getByRole('region', { name: 'Search Terms Reports' });
  await expect(list.getByRole('row')).toHaveCount(3);

  await tabTo(page, page.getByRole('link', { name: 'New report' }), 60);
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('heading', { level: 1, name: 'New Search Terms Report' }),
  ).toBeVisible();

  const name = page.getByRole('textbox', { name: 'Report name' });
  await tabTo(page, name, 60);
  await page.keyboard.press('Control+KeyA');
  await page.keyboard.type('Contract terms');
  // Scope: Saved search is preselected; choose one with the arrow keys.
  const saved = page.getByRole('combobox', { name: 'Saved search' });
  await tabTo(page, saved);
  for (let i = 0; i < 6 && (await saved.inputValue()) !== 'ss-1'; i++)
    await page.keyboard.press('ArrowDown');
  await expect(saved).toHaveValue('ss-1');

  const terms = page.getByRole('textbox', { name: /Terms, one per line/ });
  await tabTo(page, terms);
  await page.keyboard.type('terminat*');
  await page.keyboard.press('Enter');
  await page.keyboard.type('"breach of contract');
  await page.keyboard.press('Enter');
  await page.keyboard.type('penalt* OR liquidated');
  await expect(page.getByRole('heading', { name: 'Preview: 3 terms' })).toBeVisible();

  await tabTo(page, page.getByRole('button', { name: 'Run report' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/searches\/terms-reports\/str-new-1$/);
  const created = mock.termReports.requests.find((r) => r.method === 'POST');
  expect(created?.body).toMatchObject({
    name: 'Contract terms',
    scope: { kind: 'savedSearch', id: 'ss-1' },
    terms: [
      { name: 'terminat*', expression: 'terminat*' },
      { name: '"breach of contract', expression: '"breach of contract' },
      { name: 'penalt* OR liquidated', expression: 'penalt* OR liquidated' },
    ],
  });
  expect(created?.idempotencyKey).toBeTruthy();

  // Progress with a link to its job, then the results.
  await expect(page.getByRole('heading', { level: 1, name: 'Contract terms' })).toBeVisible();
  await expect(page.getByText('You can leave this page: the report keeps running.')).toBeVisible();
  await expect(page.getByRole('progressbar', { name: 'Report progress' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'View in Jobs' }).first()).toHaveAttribute(
    'href',
    '/w/ws-1/jobs/job-str-new-1',
  );
  await expect(page.getByRole('button', { name: 'Re-run on the same set' })).toBeVisible({
    timeout: 15_000,
  });
  const results = page.getByRole('region', { name: 'Counts per term' });
  await expect(results.getByRole('row', { name: /breach of contract/ })).toContainText(
    'Syntax error at character 1',
  );
  await expect(page.getByText('1 term has a syntax error and was not counted.')).toBeVisible();

  // Open a term's documents from its count.
  const count = results.getByRole('link', { name: /documents with hits for terminat\*/ });
  await tabTo(page, count, 60);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/documents\?termReport=str-new-1&term=str-new-1-t1$/);
  const panel = page.getByRole('group', { name: 'Search Terms Report term' });
  await expect(panel).toContainText('Contract terms');
  await expect(panel).toContainText('terminat*');
  await expect(page.getByRole('grid')).toBeVisible();
});

test('shows the frozen set, whose view the counts are, totals, sorting, exports and re-run', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/searches/terms-reports/str-1');
  const about = page.getByRole('region', { name: 'About this report' });
  await expect(about).toContainText('snapshot-str-1');
  await expect(about).toContainText('Taken Oct 3, 2026');
  await expect(about).toContainText('250 documents');
  await expect(about).toContainText(
    'Counts include only the documents you could see when you ran this report.',
  );
  await expect(about).not.toContainText('was not up to date');

  const table = page.getByRole('region', { name: 'Counts per term' });
  await expect(table.locator('tfoot')).toContainText('Documents in scope');
  await expect(table.locator('tfoot')).toContainText('250');
  // The term with an error stays listed, last when sorting.
  const header = table.getByRole('columnheader', { name: /Documents with hits/ });
  await header.getByRole('button').click();
  await expect(header).toHaveAttribute('aria-sort', 'descending');
  await expect(table.locator('tbody tr').last()).toContainText('Unbalanced phrase');

  // Downloads go through the protected-content gateway (a browser download, not a page request).
  await expect(page.getByRole('link', { name: 'Export CSV' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/search-term-reports/str-1/export?format=csv',
  );
  await expect(page.getByRole('link', { name: 'Export XLSX' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/search-term-reports/str-1/export?format=xlsx',
  );

  await page.getByRole('button', { name: 'Re-run on the same set' }).click();
  await expect(toast(page, 'Re-running the report on the same frozen set.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Re-run on the same set' })).toBeVisible({
    timeout: 15_000,
  });
  const rerun = mock.termReports.requests.find((r) => r.path.endsWith('/rerun'));
  expect(rerun?.idempotencyKey).toBeTruthy();
  // Same frozen set, same numbers.
  await expect(about).toContainText('snapshot-str-1');
  await expect(table.locator('tfoot')).toContainText('250');
});

test('says plainly when the index was not current and that counts are the runner’s', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/searches/terms-reports/str-2');
  await expect(page.getByText('The search index was not up to date.')).toBeVisible();
  await expect(
    page
      .getByRole('region', { name: 'About this report' })
      .getByText('only the documents Jamie Lee could see when they ran this report'),
  ).toBeVisible();
});

test('Saved Searches › Add to new Search Terms Report pre-fills the scope', async ({ page }) => {
  await openPage(page, '/w/ws-1/searches/saved');
  await page.getByRole('button', { name: 'Actions for Responsive emails' }).click();
  const item = page
    .getByRole('menu', { name: 'Actions for Responsive emails' })
    .getByRole('menuitem', { name: /Add to new Search Terms Report/ });
  await expect(item).not.toHaveAttribute('aria-disabled', 'true');
  await item.click();
  await expect(page).toHaveURL(/\/searches\/terms-reports\/new\?savedSearch=ss-1$/);
  await expect(page.getByRole('combobox', { name: 'Saved search' })).toHaveValue('ss-1');
});

test.describe('without admin rights', () => {
  test.use({
    api: {
      permissions: ALL_PERMISSIONS.filter(
        (p) =>
          p !== 'Workspace.ManageSecurity' && p !== 'Job.ViewAll' && p !== 'Workspace.ManageUsers',
      ),
    },
  });

  test('cannot delete another person’s report and sees no generation numbers', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/terms-reports/str-2');
    await expect(page.getByRole('heading', { level: 1, name: 'Privilege screen' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Delete' })).toHaveCount(0);
    await expect(page.getByText('Search generation')).toHaveCount(0);
  });
});
