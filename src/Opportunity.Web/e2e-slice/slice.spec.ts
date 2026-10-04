import type { Page } from '@playwright/test';
import { expect, test } from './support/fixtures';
import {
  allSelected,
  docOf,
  exportFrozenSet,
  frozenSetCreated,
  runSearch,
  searchUntil,
} from './support/path';
import { Api, documentsPath, loadRun, scope, storageState, type RunInfo } from './support/slice';

// Baseline §32 with the mouse, on the real stack: search → view → interactive code (Save & Next) → search the new
// coding → Mass Edit the frozen set → verify by search → export the frozen set and download it through the gateway.
// The reviewer codes; the workspace admin mass edits and exports (Reviewers hold no Coding.Bulk or Export.Create).

test.describe.configure({ mode: 'serial' });

const term = 'quillfarrow';
let run: RunInfo;
let codedControlNumber: string;

test.beforeAll(() => {
  run = loadRun();
});

test.describe('as a reviewer', () => {
  test.use({ storageState: storageState('reviewer') });

  test('searches, views the extracted text, codes with Save & Next and finds the new coding by search', async ({
    page,
  }) => {
    const expected = run.needles[term]!;
    await page.goto(documentsPath);
    await expect(page.getByRole('heading', { level: 1, name: 'Documents' })).toBeVisible();

    // Search.
    await runSearch(page, `${term} AND ${scope(run)}`);
    await expect(page.locator('.grid__count')).toContainText(`${expected.length} documents`);

    // View: the first hit opens in Review mode on its extracted text, with the search hit in it.
    const grid = page.getByRole('grid', { name: 'Documents' });
    await grid.getByRole('row').filter({ hasText: run.prefix }).first().dblclick();
    const viewer = page.getByRole('region', { name: 'Viewer' });
    const position = page.locator('.review__position');
    await expect(position).toHaveText(docOf(1, expected.length));
    codedControlNumber = (await page.locator('.review__control').textContent())!.trim();
    expect(expected).toContain(codedControlNumber);
    await expect(
      viewer
        .getByRole('tablist', { name: 'Viewer mode' })
        .getByRole('tab', { name: 'Extracted Text' }),
    ).toHaveAttribute('aria-selected', 'true');
    const text = viewer.getByLabel(`Extracted text of ${codedControlNumber}`);
    await expect(text).toContainText(term);
    // The search hits are highlighted in the text and stepped through.
    const hits = viewer.getByRole('group', { name: 'Search hits' });
    await expect(hits).toContainText(/\d+ search hits?/);
    await hits.getByRole('button', { name: 'Next hit' }).click();
    await expect(hits).toContainText(/Hit 1 of \d+/);

    // Interactive code, then Save & Next.
    const coding = page.getByRole('region', { name: 'Coding' });
    await coding.getByRole('radio', { name: 'Responsive', exact: true }).check();
    await coding.getByRole('textbox', { name: 'Reviewer Comments' }).fill(`Slice ${run.prefix}`);
    await expect(coding.getByText('Unsaved changes')).toBeVisible();
    const saved = page.waitForResponse(
      (r) => r.request().method() === 'PUT' && /\/documents\/[^/]+\/coding$/.test(r.url()),
    );
    await coding.getByRole('button', { name: 'Save & Next' }).click();
    expect((await saved).status()).toBe(200);
    await expect(position).toHaveText(docOf(2, expected.length));
    await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).not.toBeChecked();

    // Back on the first document its saved coding is shown.
    await page.getByRole('button', { name: 'Previous document' }).click();
    await expect(position).toHaveText(docOf(1, expected.length));
    await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).toBeChecked();

    // Search the new coding once it is searchable (field:value).
    await page.getByRole('button', { name: 'Back to list' }).click();
    await searchUntil(page, `responsiveness:Responsive AND ${scope(run)}`, 1);
    await expect(grid.getByRole('row').filter({ hasText: run.prefix })).toHaveText([
      new RegExp(codedControlNumber),
    ]);
    await searchUntil(page, `reviewer_comments:"Slice ${run.prefix}" AND ${scope(run)}`, 1);
  });
});

test.describe('as the workspace admin', () => {
  test.use({ storageState: storageState('admin') });

  test('mass edits all results as a frozen set, verifies it by search, exports the set and downloads the package', async ({
    page,
  }) => {
    const expected = run.needles[term]!;
    await page.goto(documentsPath);
    await runSearch(page, `${term} AND ${scope(run)}`);
    await expect(page.locator('.grid__count')).toContainText(`${expected.length} documents`);

    // Select every result, then Mass Actions → Mass Edit….
    await page.getByRole('button', { name: 'Select', exact: true }).click();
    await page
      .getByRole('menuitem', { name: new RegExp(`^All (≈ )?${expected.length} results$`) })
      .click();
    await expect(page.getByText(allSelected(expected.length))).toBeVisible();
    await page.getByRole('button', { name: 'Mass Actions' }).click();
    await page.getByRole('menuitem', { name: 'Mass Edit…' }).click();
    const frozenSet = await massEdit(page, expected.length);

    // Verify the bulk result through search.
    await searchUntil(page, `issues:Pricing AND ${scope(run)}`, expected.length);
    const api = Api.of(page);
    expect(await api.count(`issues:Pricing AND ${term} AND ${scope(run)}`)).toBe(expected.length);
    // The reviewer's earlier coding is untouched by the Mass Edit (only Issues changed).
    expect(await api.count(`responsiveness:Responsive AND issues:Pricing AND ${scope(run)}`)).toBe(
      1,
    );

    // Export the frozen set and download the package through the protected-content gateway.
    await exportFrozenSet(api, frozenSet, expected, codedControlNumber);
  });
});

/** Mass Edit dialog: Issues → add Pricing, confirm the frozen set and apply. Returns the frozen set's id. */
async function massEdit(page: Page, count: number): Promise<string> {
  const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('checkbox', { name: 'Change Issues' }).check();
  await dialog.getByRole('combobox', { name: 'Pricing' }).selectOption('add');
  const frozen = frozenSetCreated(page);
  await dialog.getByRole('button', { name: 'Continue' }).click();
  const frozenSetId = await frozen();

  await expect(dialog.getByRole('heading', { name: 'Frozen set' })).toBeVisible();
  await expect(dialog.locator('.me__count')).toHaveText(`${count} documents`);
  await expect(dialog).toContainText('Issues: add Pricing');
  // Typed confirmation is only asked above 10,000 documents; type the count if this stack asks for it.
  const typed = dialog.locator('[data-typed] input');
  if (await typed.isVisible()) await typed.fill(String(count));
  await dialog.getByRole('button', { name: `Apply to ${count} documents` }).click();
  await expect(dialog.getByRole('heading', { name: 'Mass Edit finished' })).toBeVisible({
    timeout: 120_000,
  });
  await expect(dialog.getByRole('definition').first()).toHaveText(String(count));
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  return frozenSetId;
}
