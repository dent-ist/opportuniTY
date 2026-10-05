import type { Locator, Page } from '@playwright/test';
import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Searches › Saved Searches and the Documents saved-search pane (E16-T11), keyboard only: create, rename, move, share,
// run, and save the current search from Documents. The mock serves the wave-9 saved-search contract.

/** Opens a row's actions menu with the keyboard and activates `item`. */
async function rowAction(page: Page, name: string, item: string | RegExp): Promise<void> {
  await tabTo(page, page.getByRole('button', { name: `Actions for ${name}` }), 80);
  await page.keyboard.press('Enter');
  const menu = page.getByRole('menu', { name: `Actions for ${name}` });
  await expect(menu).toBeVisible();
  const target = menu.getByRole('menuitem', { name: item });
  for (let i = 0; i < 12 && !(await isFocused(target)); i++) await page.keyboard.press('ArrowDown');
  await expect(target).toBeFocused();
  await page.keyboard.press('Enter');
}

const isFocused = (l: Locator) => l.evaluate((el) => el === document.activeElement);

/** A notification toast with `text` (the live region repeats it for screen readers). */
const toast = (page: Page, text: string) =>
  page.getByRole('region', { name: 'Notifications' }).getByText(text);

test('creates, renames, moves, shares and runs a saved search with the keyboard only (E16-T11)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/searches/saved');
  await expect(page.getByRole('heading', { level: 1, name: 'Saved Searches' })).toBeVisible();
  const list = page.getByRole('region', { name: 'All saved searches' });
  await expect(list.getByRole('row')).toHaveCount(5);
  // Live vs frozen, owner, sharing, last hit count with its freshness.
  await expect(list.getByRole('row', { name: /Termination clauses/ })).toContainText('≥ 10,000');
  await expect(list.getByRole('row', { name: /Termination clauses/ })).toContainText(
    'Updating as of',
  );
  await expect(list.getByRole('row', { name: /Privilege candidates/ })).toContainText('Jamie Lee');
  await expect(list.getByRole('row', { name: /Privilege candidates/ })).toContainText(
    'Results filtered for you',
  );

  // Create.
  await tabTo(page, page.getByRole('button', { name: 'New saved search' }));
  await page.keyboard.press('Enter');
  const editor = page.getByRole('dialog', { name: 'New saved search' });
  await expect(editor.getByRole('textbox', { name: 'Name' })).toBeFocused();
  await page.keyboard.type('Custodian review');
  await tabTo(page, editor.getByRole('combobox', { name: 'Folder' }));
  await page.keyboard.press('ArrowDown');
  await expect(editor.getByRole('combobox', { name: 'Folder' })).toHaveValue('folder-1');
  await tabTo(page, editor.getByRole('textbox', { name: 'Keyword search' }));
  await page.keyboard.type('custodian:smith');
  await expect(editor.getByRole('group', { name: 'Conditions' })).toContainText(
    'not available in this version',
  );
  await page.keyboard.press('Enter');
  await expect(editor).toBeHidden();
  await expect(toast(page, 'Saved search “Custodian review” created.')).toBeVisible();
  const created = mock.savedSearches.requests.find((r) => r.method === 'POST');
  expect(created?.body).toMatchObject({
    name: 'Custodian review',
    folderId: 'folder-1',
    query: 'custodian:smith',
  });
  await expect(list.getByRole('row', { name: /Custodian review/ })).toContainText('First pass');

  // Rename (If-Match carries the version).
  await rowAction(page, 'Custodian review', 'Rename…');
  const rename = page.getByRole('dialog', { name: 'Rename saved search' });
  const name = rename.getByRole('textbox', { name: 'Name' });
  await expect(name).toBeFocused();
  await page.keyboard.press('Control+KeyA');
  await page.keyboard.type('Smith custodian review');
  await page.keyboard.press('Enter');
  await expect(rename).toBeHidden();
  await expect(toast(page, 'Renamed to “Smith custodian review”.')).toBeVisible();
  const renamed = mock.savedSearches.requests.filter((r) => r.method === 'PUT').at(-1);
  expect(renamed?.ifMatch).toBe('"1"');
  expect(renamed?.body).toMatchObject({ name: 'Smith custodian review', folderId: 'folder-1' });

  // Move to another folder.
  await rowAction(page, 'Smith custodian review', 'Move…');
  const move = page.getByRole('dialog', { name: 'Move saved search' });
  const folder = move.getByRole('combobox', { name: 'Folder' });
  await expect(folder).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(folder).toHaveValue('folder-3');
  await tabTo(page, move.getByRole('button', { name: 'Move', exact: true }));
  await page.keyboard.press('Enter');
  await expect(move).toBeHidden();
  await expect(list.getByRole('row', { name: /Smith custodian review/ })).toContainText(
    'First pass / Hot documents',
  );

  // Share with a group.
  await rowAction(page, 'Smith custodian review', 'Share…');
  const share = page.getByRole('dialog', { name: 'Share Smith custodian review' });
  await expect(share).toContainText('sharing never gives access to documents');
  await tabTo(page, share.getByRole('combobox', { name: 'Add a user or group' }));
  await page.keyboard.press('ArrowDown');
  await tabTo(page, share.getByRole('button', { name: 'Add', exact: true }));
  await page.keyboard.press('Enter');
  await expect(share.getByRole('listitem')).toContainText('Review Team');
  await tabTo(page, share.getByRole('button', { name: 'Save sharing' }));
  await page.keyboard.press('Enter');
  await expect(share).toBeHidden();
  const shared = mock.savedSearches.requests.find((r) => r.path.endsWith('/sharing'));
  expect(shared?.body).toEqual({ sharedWith: [{ kind: 'group', id: 'Review Team' }] });
  await expect(list.getByRole('row', { name: /Smith custodian review/ })).toContainText(
    'Shared with Review Team',
  );

  // Run: Documents with the saved search applied, run by id.
  await tabTo(page, list.getByRole('link', { name: 'Smith custodian review' }), 80);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/documents\?savedSearch=ss-100$/);
  const panel = page.getByRole('group', { name: 'Saved search' });
  await expect(panel).toContainText('Saved search (live): Smith custodian review');
  await expect(panel.getByRole('button', { name: 'Clear saved search' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Keyword' })).toHaveValue('custodian:smith');
  await expect(page.locator('.grid__count')).toContainText('12 documents');
  expect(mock.savedSearches.runs).toEqual(['ss-100']);
});

test('runs a saved search from the Documents pane and saves the current search, keyboard only (E16-T11)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const pane = page.getByRole('complementary', { name: 'Saved Searches' });
  const tree = pane.getByRole('tree', { name: 'Saved searches' });
  await tabTo(page, tree.getByRole('treeitem', { name: 'First pass' }));
  await page.keyboard.press('ArrowDown'); // Hot documents (folder)
  await page.keyboard.press('ArrowDown'); // Responsive emails
  await expect(tree.getByRole('treeitem', { name: 'Responsive emails' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/savedSearch=ss-1$/);
  await expect(page.getByRole('group', { name: 'Saved search' })).toContainText(
    'Responsive emails',
  );
  await expect(tree.getByRole('treeitem', { name: 'Responsive emails' })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  expect(mock.savedSearches.runs).toEqual(['ss-1']);

  // A shared search from a colleague says that results are filtered for the person running it.
  await tree
    .getByRole('treeitem', { name: /Privilege/ })
    .first()
    .focus();
  // Top-level folders start open: Right moves to the folder's first item.
  await page.keyboard.press('ArrowRight');
  await expect(tree.getByRole('treeitem', { name: /Privilege candidates/ })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('group', { name: 'Saved search' })).toContainText(
    'Shared by Jamie Lee. Results are filtered to the documents you may see.',
  );

  // A new keyword search leaves the saved search; Save current search keeps it.
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await page.keyboard.press('Alt+Shift+KeyK');
  await expect(keyword).toBeFocused();
  await page.keyboard.press('Control+KeyA');
  await page.keyboard.type('contract');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('group', { name: 'Saved search' })).toBeHidden();
  await expect(page).not.toHaveURL(/savedSearch/);

  await page.locator('main#main').focus();
  await tabTo(page, pane.getByRole('button', { name: 'Save current search' }));
  await page.keyboard.press('Enter');
  const editor = page.getByRole('dialog', { name: 'New saved search' });
  await expect(editor.getByRole('textbox', { name: 'Name' })).toBeFocused();
  await expect(editor.getByRole('textbox', { name: 'Keyword search' })).toHaveValue('contract');
  await page.keyboard.type('Contract documents');
  await page.keyboard.press('Enter');
  await expect(editor).toBeHidden();
  const created = mock.savedSearches.requests.find((r) => r.method === 'POST');
  expect(created?.body).toMatchObject({ name: 'Contract documents', query: 'contract' });
  await expect(page.getByRole('group', { name: 'Saved search' })).toContainText(
    'Contract documents',
  );
  await expect(page).toHaveURL(/savedSearch=ss-100$/);
  await expect(tree.getByRole('treeitem', { name: 'Contract documents' })).toBeVisible();
  await expect(keyword).toHaveValue('contract');
});

test('Mass Edit results opens Mass Edit over every result of the saved search (E16-T11)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/searches/saved');
  await rowAction(page, 'Responsive emails', 'Mass Edit results…');
  const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('all 12 results');
  await expect(page).not.toHaveURL(/then=/);
});

test('Export from a saved search says why it is not available (E16-T11)', async ({ page }) => {
  await openPage(page, '/w/ws-1/searches/saved');
  await rowAction(page, 'PDF attachments', /Export results/);
  await expect(
    toast(page, 'Export from a saved search is not available in this version.'),
  ).toBeVisible();
});

test.describe('without SavedSearch.Share or admin rights', () => {
  test.use({
    api: {
      permissions: ALL_PERMISSIONS.filter(
        (p) => p !== 'SavedSearch.Share' && p !== 'Workspace.ManageSecurity',
      ),
    },
  });

  test('offers only the actions the user has (E16-T11)', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/saved');
    await page.getByRole('button', { name: 'Actions for Responsive emails' }).click();
    const own = page.getByRole('menu', { name: 'Actions for Responsive emails' });
    await expect(own.getByRole('menuitem', { name: 'Rename…' })).toBeVisible();
    await expect(own.getByRole('menuitem', { name: 'Share…' })).toHaveCount(0);
    await page.keyboard.press('Escape');

    await page.getByRole('button', { name: 'Actions for Privilege candidates' }).click();
    const theirs = page.getByRole('menu', { name: 'Actions for Privilege candidates' });
    await expect(theirs.getByRole('menuitem', { name: 'Run in Documents' })).toBeVisible();
    await expect(theirs.getByRole('menuitem', { name: 'Copy…' })).toBeVisible();
    for (const hidden of ['Edit…', 'Rename…', 'Move…', 'Share…', 'Delete…']) {
      await expect(theirs.getByRole('menuitem', { name: hidden })).toHaveCount(0);
    }
  });
});
