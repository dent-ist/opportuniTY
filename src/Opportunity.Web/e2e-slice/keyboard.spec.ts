import type { Page } from '@playwright/test';
import { allSelected, docOf, exportFrozenSet, frozenSetCreated, searchUntil } from './support/path';
import { expect, test } from './support/fixtures';
import { Api, loadRun, scope, signIn, tabTo, type RunInfo } from './support/slice';

// The §32 path with the keyboard only (E15-T04, WCAG 2.1.1/2.4.3/2.4.7) on the real stack: sign in through Keycloak,
// open the workspace, search, view, code with the coding shortcuts and Save & Next, search the coding, then (as the
// admin) select all results, Mass Edit and verify. The mouse is never used. The export has no UI yet; it runs through
// the API as in the mouse path.

test.describe.configure({ mode: 'serial' });

const term = 'brindlequost';
let run: RunInfo;
let codedControlNumber: string;

test.beforeAll(() => {
  run = loadRun();
});

/** Focuses the keyword box (Alt+Shift+K), replaces its query and runs it with Enter. */
async function keyboardSearch(page: Page, query: string): Promise<void> {
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await page.keyboard.press('Alt+Shift+KeyK');
  await expect(keyword).toBeFocused();
  await page.keyboard.press('ControlOrMeta+KeyA');
  await page.keyboard.press('Delete');
  await page.keyboard.type(query);
  const searched = page.waitForResponse(
    (r) => r.request().method() === 'POST' && r.url().endsWith('/searches') && r.ok(),
  );
  await page.keyboard.press('Enter');
  await searched;
}

/** Signs in with the keyboard and opens the demo workspace's documents from the workspace list. */
async function openWorkspace(page: Page, user: 'reviewer' | 'admin'): Promise<void> {
  await signIn(page, user, { keyboard: true });
  await tabTo(page, page.getByRole('link', { name: 'Demo workspace' }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1, name: 'Documents' })).toBeVisible();
}

test('a reviewer searches, views, codes with Save & Next and finds the coding with the keyboard only', async ({
  page,
}) => {
  const expected = run.needles[term]!;
  await openWorkspace(page, 'reviewer');
  await keyboardSearch(page, `${term} AND ${scope(run)}`);
  await expect(page.locator('.grid__count')).toContainText(`${expected.length} documents`);

  // The list, then Enter opens the active (first) row in Review mode with focus in the viewer.
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  await page.keyboard.press('Enter');
  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer).toBeFocused();
  await expect(page.locator('.review__position')).toHaveText(docOf(1, expected.length));
  codedControlNumber = (await page.locator('.review__control').textContent())!.trim();
  expect(expected).toContain(codedControlNumber);
  await expect(viewer.getByLabel(`Extracted text of ${codedControlNumber}`)).toContainText(term);
  const hits = viewer.getByRole('group', { name: 'Highlights' });
  await expect(hits).toContainText(/Search hits \([1-9]\d*\)/);
  await page.keyboard.press('F3');
  await expect(hits).toContainText(/Hit 1 of \d+/);

  // Alt+Shift+C, 1: the first field (Responsiveness); 2 picks Not Responsive. Ctrl+Enter is Save & Next.
  const coding = page.getByRole('region', { name: 'Coding' });
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit2');
  await expect(coding.getByRole('radio', { name: 'Not Responsive' })).toBeChecked();
  const saved = page.waitForResponse(
    (r) => r.request().method() === 'PUT' && /\/documents\/[^/]+\/coding$/.test(r.url()),
  );
  await page.keyboard.press('Control+Enter');
  expect((await saved).status()).toBe(200);
  await expect(page.locator('.review__position')).toHaveText(docOf(2, expected.length));

  // Escape goes back to the list, focus on it; the coding is found once it is searchable.
  await page.keyboard.press('Escape');
  await expect(grid).toBeFocused();
  await searchUntil(
    page,
    `responsiveness:"Not Responsive" AND ${term} AND ${scope(run)}`,
    1,
    keyboardSearch,
  );
  await expect(grid.getByRole('row').filter({ hasText: run.prefix })).toHaveText([
    new RegExp(codedControlNumber),
  ]);
});

test('the workspace admin selects all results, mass edits the frozen set and verifies it with the keyboard only', async ({
  page,
}) => {
  const expected = run.needles[term]!;
  await openWorkspace(page, 'admin');
  await keyboardSearch(page, `${term} AND ${scope(run)}`);
  await expect(page.locator('.grid__count')).toContainText(`${expected.length} documents`);

  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  await page.keyboard.press('Alt+Shift+KeyA');
  await expect(page.getByText(allSelected(expected.length))).toBeVisible();

  // Alt+Shift+E: Mass Edit. Issues → add Contracts; Continue freezes the set; Apply.
  await page.keyboard.press('Alt+Shift+KeyE');
  const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
  await expect(dialog).toBeVisible();
  await tabTo(page, dialog.getByRole('checkbox', { name: 'Change Issues' }));
  await page.keyboard.press('Space');
  const contracts = dialog.getByRole('combobox', { name: 'Contracts' });
  await tabTo(page, contracts);
  await page.keyboard.press('ArrowDown');
  await expect(contracts).toHaveValue('add');
  const frozen = frozenSetCreated(page);
  await tabTo(page, dialog.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');
  const frozenSetId = await frozen();
  await expect(dialog.getByRole('heading', { name: 'Frozen set' })).toBeFocused();
  await expect(dialog.locator('.me__count')).toHaveText(`${expected.length} documents`);
  await expect(dialog).toContainText('Issues: add Contracts');
  await tabTo(page, dialog.getByRole('button', { name: `Apply to ${expected.length} documents` }));
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('heading', { name: 'Mass Edit finished' })).toBeVisible({
    timeout: 120_000,
  });
  await expect(dialog.getByRole('definition').first()).toHaveText(String(expected.length));
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(grid).toBeFocused();

  // Verify through search, then export the frozen set (API: no export UI yet).
  await searchUntil(page, `issues:Contracts AND ${scope(run)}`, expected.length, keyboardSearch);
  const api = Api.of(page);
  expect(await api.count(`issues:Contracts AND ${term} AND ${scope(run)}`)).toBe(expected.length);
  await exportFrozenSet(api, frozenSetId, expected, codedControlNumber, {
    issue: 'Contracts',
    responsiveness: 'Not Responsive',
  });
});
