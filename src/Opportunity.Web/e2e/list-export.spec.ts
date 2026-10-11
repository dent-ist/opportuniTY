import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { tabTo } from './support/tab';
import type { Theme } from '../src/app/core/preferences/ui-preferences';

// Export List (CSV) (#181) against the mock (e2e/support/mock-list-exports.ts): Mass Actions › Export List (CSV)… writes
// the current view's columns for the selection, or the whole list, as a background job; the dialog follows it and the
// file is a gateway link. Keyboard only, then axe over both dialog states in every theme.

test('exports the checked rows to CSV with the keyboard only and downloads the file; focus returns to the list', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  await page.keyboard.press('Space');
  await expect(page.getByText('Selected: 1', { exact: true })).toBeVisible();

  await tabTo(page, page.getByRole('button', { name: 'Mass Actions' }));
  await page.keyboard.press('Enter');
  const item = page.getByRole('menuitem', { name: 'Export List (CSV)…' });
  await expect(item).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(item).toBeFocused();
  await page.keyboard.press('Enter');

  const dialog = page.getByRole('dialog', { name: 'Export List (CSV)' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('radio', { name: 'Selected documents (1)' })).toBeChecked();
  await expect(
    dialog.getByRole('radio', { name: 'All results of the list (250)' }),
  ).not.toBeChecked();
  await expect(dialog.getByRole('list')).toContainText('Control Number');
  await expect(dialog.getByRole('list')).toContainText('File Name');

  await tabTo(page, dialog.getByRole('button', { name: 'Export' }));
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('status')).toContainText('Ready: 0 rows. 1 document was left out');
  expect(mock.listExports.requests).toHaveLength(1);
  const body = mock.listExports.requests[0].body;
  expect(body['documentIds']).toEqual(['doc-1']);
  expect(body['query']).toBeUndefined();
  expect((body['fieldIds'] as unknown[]).length).toBeGreaterThan(0);
  expect(mock.listExports.requests[0].idempotencyKey).toBeTruthy();

  const link = dialog.getByRole('link', { name: /Download CSV/ });
  await expect(link).toHaveAttribute('href', '/api/v1/workspaces/ws-1/list-exports/lx-1/content');
  await tabTo(page, link);
  const download = page.waitForEvent('download');
  await page.keyboard.press('Enter');
  // The browser fetches the gateway link as a download (the real gateway names it export-list-….csv).
  expect((await download).url()).toMatch(
    /\/api\/v1\/workspaces\/ws-1\/list-exports\/lx-1\/content$/,
  );

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(grid).toBeFocused();
});

test('without a selection the whole list goes as its query', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/documents');
  await page.getByRole('button', { name: 'Mass Actions' }).click();
  await page.getByRole('menuitem', { name: 'Export List (CSV)…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Export List (CSV)' });
  await expect(dialog.getByRole('radio', { name: 'All results of the list (250)' })).toBeChecked();
  await dialog.getByRole('button', { name: 'Export' }).click();
  await expect(dialog.getByRole('status')).toContainText('Ready: 249 rows.');
  expect(mock.listExports.requests[0].body['query']).toBe('');
  expect(mock.listExports.requests[0].body['documentIds']).toBeUndefined();
});

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

for (const theme of THEMES) {
  test(`axe, ${theme} theme: the Export List dialog before and after the export`, async ({
    page,
  }, testInfo) => {
    await useTheme(page, theme);
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('button', { name: 'Mass Actions' }).click();
    await expect(page.getByRole('menuitem', { name: 'Export List (CSV)…' })).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
    await page.getByRole('menuitem', { name: 'Export List (CSV)…' }).click();
    const dialog = page.getByRole('dialog', { name: 'Export List (CSV)' });
    await expect(dialog).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
    await dialog.getByRole('button', { name: 'Export' }).click();
    await expect(dialog.getByRole('link', { name: /Download CSV/ })).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
  });
}
