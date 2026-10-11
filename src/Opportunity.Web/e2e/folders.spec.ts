import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { tabTo } from './support/tab';

// The read-only Folders tree in the Documents browser (#179, Q-55), keyboard only: open a folder (its subfolders load
// on first open), list a folder's documents, include or exclude subfolders, clear the folder. axe in every theme with
// a folder selected.

test('browses imported folders and lists a folder’s documents with the keyboard only (#179)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const pane = page.getByRole('complementary', { name: 'Browser' });
  const tree = pane.getByRole('tree', { name: 'Folders' });
  await expect(tree.getByRole('treeitem', { name: /^Custodians/ })).toBeVisible();
  expect(mock.folders.levels).toEqual([null]);

  await tabTo(page, tree.getByRole('treeitem', { name: /^Custodians/ }), 60);
  await page.keyboard.press('ArrowRight');
  const smith = tree.getByRole('treeitem', { name: /^Smith/ });
  await expect(smith).toBeVisible();
  expect(mock.folders.levels).toEqual([null, 'fld-custodians']);
  await expect(smith).toHaveAccessibleName('Smith 22 documents');

  await page.keyboard.press('ArrowDown');
  await expect(smith).toBeFocused();
  await page.keyboard.press('Enter');
  const panel = page.getByRole('group', { name: 'Folder', exact: true });
  await expect(panel).toContainText('Custodians\\Smith');
  await expect(smith).toHaveAttribute('aria-selected', 'true');
  expect(mock.lastSearch()).toMatchObject({
    query: '',
    folder: { folderId: 'fld-smith', includeSubfolders: true },
  });

  // Only the folder itself: the list runs again and the tree counts follow.
  await tabTo(page, panel.getByRole('checkbox', { name: 'Include subfolders' }), 60);
  await page.keyboard.press('Space');
  await expect
    .poll(() => mock.lastSearch())
    .toMatchObject({
      folder: { folderId: 'fld-smith', includeSubfolders: false },
    });
  await expect(smith).toHaveAccessibleName('Smith 4 documents');

  // A keyword search narrows the list within the folder.
  await page.keyboard.press('Alt+Shift+KeyK');
  await page.keyboard.type('memo');
  await page.keyboard.press('Enter');
  await expect
    .poll(() => mock.lastSearch())
    .toMatchObject({
      query: 'memo',
      folder: { folderId: 'fld-smith', includeSubfolders: false },
    });

  await tabTo(page, panel.getByRole('button', { name: 'Clear folder' }), 60, { backwards: true });
  await page.keyboard.press('Enter');
  await expect(panel).toBeHidden();
  expect(mock.lastSearch()).not.toHaveProperty('folder');
});

for (const theme of ['light', 'dark', 'high-contrast'] as const) {
  test(`axe with a folder selected, ${theme} theme`, async ({ page }, testInfo) => {
    await useTheme(page, theme);
    await openPage(page, '/w/ws-1/documents');
    const tree = page.getByRole('tree', { name: 'Folders' });
    await tree.getByRole('treeitem', { name: /^Loose Files/ }).click();
    await expect(page.getByRole('group', { name: 'Folder', exact: true })).toBeVisible();
    await tree
      .getByRole('treeitem', { name: /^Custodians/ })
      .locator('.tree__toggle')
      .click();
    await expect(tree.getByRole('treeitem', { name: /^Lee/ })).toBeVisible();
    await expectNoSeriousAxeViolations(page, testInfo);
  });
}
