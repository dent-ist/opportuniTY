import type { Page } from '@playwright/test';
import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Document-list Views (E16-T09), keyboard only: choose and reorder columns, sort by several columns from the headers,
// save the list as a View, and find it again after a reload (the layout is remembered per user and workspace).

const headerLabels = (page: Page) =>
  page.locator('[role="columnheader"] .grid__label').allTextContents();

/** Moves the grid's active cell to the header of `label` (ArrowUp to the header row, then Arrow Left/Right). */
async function focusHeader(page: Page, label: string): Promise<void> {
  const grid = page.getByRole('grid', { name: 'Documents' });
  await expect(grid).not.toHaveAttribute('aria-busy', 'true');
  await grid.focus();
  const active = () => grid.getAttribute('aria-activedescendant');
  /** Presses `key` and waits until the grid has moved its active cell (or stays put at an edge). */
  const step = async (key: string) => {
    const before = await active();
    await page.keyboard.press(key);
    await expect
      .poll(active, { timeout: 2_000 })
      .not.toBe(before)
      .catch(() => undefined);
  };
  await step('ArrowUp');
  for (let i = 0; i < 12 && !(await active())?.endsWith('-c0'); i++) await step('ArrowLeft');
  for (let i = 0; i < 12; i++) {
    const id = await active();
    const text = await page.locator(`[id="${id}"] .grid__label`).allTextContents();
    if (text[0]?.trim() === label) return;
    await step('ArrowRight');
  }
  throw new Error(`Header ${label} not reached`);
}

test('chooses, reorders and sorts columns, saves a view and finds it again, keyboard only (E16-T09)', async ({
  page,
  mock,
}) => {
  test.setTimeout(90_000);
  await openPage(page, '/w/ws-1/documents');
  await expect(page.getByRole('grid', { name: 'Documents' })).toBeVisible();
  expect((await headerLabels(page)).slice(1)).toEqual([
    'Document Date',
    'File Name',
    'File Type',
    'File Extension',
    'File Size',
    'Page Count',
  ]);

  // Columns dialog: move File Name up, add a coding column, sort by File Size descending.
  await tabTo(page, page.getByRole('button', { name: 'Columns', exact: true }), 80);
  await page.keyboard.press('Enter');
  const chooser = page.getByRole('dialog', { name: 'Columns and sort' });
  await expect(chooser).toBeVisible();
  await tabTo(page, chooser.getByRole('button', { name: 'Move File Name up' }));
  await page.keyboard.press('Enter');
  await expect(chooser.locator('.cc__item .cc__label').first()).toHaveText('File Name');
  await expect(chooser.getByRole('button', { name: 'Move File Name down' })).toBeFocused();
  await tabTo(page, chooser.getByRole('button', { name: 'Remove File Extension' }));
  await page.keyboard.press('Enter');
  await tabTo(page, chooser.getByRole('button', { name: 'Responsiveness' }));
  await page.keyboard.press('Enter');
  await expect(chooser.locator('.cc__item .cc__label').last()).toHaveText('Responsiveness');
  await expect(chooser.locator('.cc__item').last()).toContainText('Not sortable');
  const level1 = chooser.getByRole('combobox', { name: 'Sort level 1' });
  await tabTo(page, level1);
  await expect(level1.locator('option', { hasText: 'Responsiveness' })).toBeDisabled();
  await page.keyboard.type('File Size');
  await expect(level1).toHaveValue('fileSize');
  await page.keyboard.press('Tab');
  await page.keyboard.type('D'); // Descending
  await expect(chooser.getByRole('combobox', { name: 'Direction, level 1' })).toHaveValue('desc');
  await tabTo(page, chooser.getByRole('button', { name: 'Apply' }));
  await page.keyboard.press('Enter');
  await expect(chooser).toBeHidden();
  await expect
    .poll(async () => (await headerLabels(page)).slice(1))
    .toEqual([
      'File Name',
      'Document Date',
      'File Type',
      'File Size',
      'Page Count',
      'Responsiveness',
    ]);
  await expect
    .poll(() => mock.lastSearch()?.['sort'])
    .toEqual([{ field: 'fileSize', direction: 'desc' }]);
  expect(mock.lastSearch()?.['fields']).toEqual(['responsiveness']);
  await expect(page.locator('.grid__modified')).toHaveText('Modified');

  // Headers: Shift+Enter adds Document Date as the second sort level; a choice column explains why it cannot sort.
  await focusHeader(page, 'Document Date');
  await page.keyboard.press('Shift+Enter');
  await expect
    .poll(() => mock.lastSearch()?.['sort'])
    .toEqual([
      { field: 'fileSize', direction: 'desc' },
      { field: 'documentDate', direction: 'asc' },
    ]);
  await expect(page.locator('[role="columnheader"]', { hasText: 'Document Date' })).toContainText(
    'sort level 2',
  );
  const responsiveness = page.locator('[role="columnheader"]', { hasText: 'Responsiveness' });
  await responsiveness.hover();
  await expect(page.getByRole('tooltip')).toContainText('Choice fields cannot be sorted');
  // Ctrl+Shift+Arrow moves a column from its header.
  await focusHeader(page, 'Page Count');
  await page.keyboard.press('Control+Shift+ArrowLeft');
  await expect
    .poll(async () => (await headerLabels(page)).slice(4, 6))
    .toEqual(['Page Count', 'File Size']);

  // Save as a shared view (this user may manage shared views).
  await tabTo(page, page.getByRole('button', { name: 'Views', exact: true }), 80);
  await page.keyboard.press('Enter');
  const menu = page.getByRole('menu', { name: 'Views' });
  await expect(menu).toBeVisible();
  const saveAs = menu.getByRole('menuitem', { name: 'Save as new view…' });
  for (let i = 0; i < 6 && !(await saveAs.evaluate((el) => el === document.activeElement)); i++) {
    await page.keyboard.press('ArrowDown');
  }
  await page.keyboard.press('Enter');
  const save = page.getByRole('dialog', { name: 'Save view' });
  await expect(save.getByRole('textbox', { name: 'View name' })).toBeFocused();
  await page.keyboard.type('Size review');
  await tabTo(page, save.getByRole('radio', { name: /Only me/ }));
  await page.keyboard.press('ArrowDown');
  await expect(save.getByRole('radio', { name: /Everyone in this workspace/ })).toBeChecked();
  await tabTo(page, save.getByRole('button', { name: 'Save view' }));
  await page.keyboard.press('Enter');
  await expect(save).toBeHidden();
  const created = mock.gridViews.writes.at(-1)!;
  expect(created.body).toMatchObject({
    name: 'Size review',
    visibility: 'shared',
    sort: [
      { field: 'fileSize', direction: 'desc' },
      { field: 'documentDate', direction: 'asc' },
    ],
  });
  const viewSelect = page.getByRole('combobox', { name: 'View', exact: true });
  await expect(viewSelect.locator('option:checked')).toHaveText('Size review');
  await expect(page.locator('.grid__modified')).toHaveCount(0);

  // The layout is remembered: after a reload the list opens on the same View, columns and sort.
  await expect.poll(() => mock.gridViews.layouts.at(-1)?.viewId).toBe('view-1');
  await openPage(page, '/w/ws-1/documents');
  await expect(viewSelect.locator('option:checked')).toHaveText('Size review');
  await expect
    .poll(async () => (await headerLabels(page)).slice(1))
    .toEqual([
      'File Name',
      'Document Date',
      'File Type',
      'Page Count',
      'File Size',
      'Responsiveness',
    ]);
  expect(mock.lastSearch()?.['sort']).toEqual([
    { field: 'fileSize', direction: 'desc' },
    { field: 'documentDate', direction: 'asc' },
  ]);

  // Switching to the shared "First pass review" view pins its coding column first and shows choice names.
  await viewSelect.focus();
  await page.keyboard.type('First pass');
  await expect(viewSelect.locator('option:checked')).toHaveText('First pass review');
  await expect
    .poll(async () => (await headerLabels(page)).slice(1))
    .toEqual(['Responsiveness', 'File Name', 'Document Date']);
  await expect(page.getByRole('row').nth(1)).toContainText(
    /Responsive|Not Responsive|Needs Further Review/,
  );
});

test.describe('without the manage-views permission', () => {
  test.use({ api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'View.ManageShared') } });

  test('saving a shared view is not offered (E16-T09)', async ({ page }) => {
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('button', { name: 'Views', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Save as new view…' }).click();
    const save = page.getByRole('dialog', { name: 'Save view' });
    await expect(save.getByRole('radio', { name: /Everyone in this workspace/ })).toBeDisabled();
    await expect(save).toContainText('needs the "Manage shared views" permission');
  });
});
