import type { Page } from '@playwright/test';
import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { tabTo } from './support/tab';
import type { Theme } from '../src/app/core/preferences/ui-preferences';

// Security-affecting coding and access-restricted states (E16-T08, familiarity guide §3.3 / §3.5, Q-11–Q-13): the
// stale-hit scenario (access revoked between search and open), a document hidden while open, a save that removes
// the reviewer's own access (confirmed, then Review moves on), the inline privilege-basis-required message, and the
// network rule: once a document answers like a missing one, nothing more is requested for it (no artifact, no
// prefetch, no view). The mock answers every route of a hidden document with the gateway's 404
// (`mock.revokeAccess`, ./support/mock-api.ts) and plays the API's 409 for an unconfirmed access-losing save.

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

/** Content artifacts of a document: text, pages and page images, natives, hits; and the view beacon. */
const ARTIFACT = /\/(text|pages|native|views)(\/|$)/;

const listRow = (page: Page, index: number) =>
  page.getByRole('grid', { name: 'Documents' }).locator('.grid__body [role="row"]').nth(index);

/** Opens row `index` (0-based) of the document list in Review mode with the keyboard. */
async function openRow(page: Page, index: number): Promise<void> {
  const grid = page.getByRole('grid', { name: 'Documents' });
  await grid.focus();
  for (let i = 0; i < index; i++) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('region', { name: 'Viewer' })).toBeVisible();
}

test('stale hit: a document hidden between search and open shows the no-access state and requests no artifact', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await expect(listRow(page, 2)).toContainText('ACM0000003');
  // Someone else's change (or a new wall) hides ACM0000003 after the list was loaded.
  mock.revokeAccess(3);
  await openRow(page, 2);

  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer.getByRole('heading', { name: 'Document not available' })).toBeVisible();
  await expect(viewer).toContainText(
    'You don’t have access to this document, or it no longer exists.',
  );
  await expect(viewer.getByRole('tab')).toHaveCount(0);
  await expect(page.locator('.review__bar')).toContainText('No longer available');
  await expect(page.getByRole('region', { name: 'Coding' })).toContainText(
    'No coding to show: this document is not available.',
  );
  await expect(page.locator('.review__bar')).not.toContainText('ACM0000003');

  // The refused first reads are all there is: no artifact, no view, and no prefetch of the next document.
  await page.waitForLoadState('networkidle');
  const refused = mock.documentRequests(3);
  expect(refused.filter((r) => ARTIFACT.test(r))).toEqual([]);
  expect(refused.every((r) => r.startsWith('GET '))).toBe(true);
  expect(mock.audit.filter((a) => a.documentId === 'doc-4')).toEqual([]);

  // Next shows ACM0000004; back on ACM0000003 nothing at all is requested.
  await page.keyboard.press(']');
  await expect(page.locator('.review__bar')).toContainText('ACM0000004');
  await expect(viewer.locator('[data-viewer-document="doc-4"]')).toHaveAttribute(
    'data-state',
    'ready',
  );
  await page.keyboard.press('[');
  await expect(viewer.getByRole('heading', { name: 'Document not available' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  expect(mock.documentRequests(3)).toEqual(refused);

  // Back in the list the row is a placeholder for the rest of the visit: no metadata, nothing to select.
  await page.keyboard.press('Escape');
  await expect(listRow(page, 2)).toContainText('No longer available');
  await expect(listRow(page, 2)).not.toContainText('ACM0000003');
  await expect(listRow(page, 2).getByRole('checkbox')).toHaveCount(0);
  await expect(listRow(page, 3)).toContainText('ACM0000004');
  expect(mock.documentRequests(3)).toEqual(refused);
});

test('saving a change that removes your own access asks first, then saves, moves on and leaves a placeholder', async ({
  page,
  mock,
}) => {
  // Coding ACM0000001 CONFIDENTIAL would hide it from this reviewer (the API decides; the mock plays it).
  mock.coding.loseAccessWhen(1005, 42);
  await openPage(page, '/w/ws-1/documents');
  await openRow(page, 0);
  const coding = page.getByRole('region', { name: 'Coding' });
  await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();
  await coding.getByRole('radio', { name: 'Responsive', exact: true }).check();
  await coding.getByRole('radio', { name: 'CONFIDENTIAL', exact: true }).check();
  await expect(coding).toContainText(
    'Your changes to Confidentiality Designation affect who may see this document.',
  );

  // Save & Next: refused until confirmed. Cancel (focused) keeps the edits and the document.
  await page.keyboard.press('Control+Enter');
  const dialog = page.getByRole('alertdialog', { name: 'Save and lose access?' });
  await expect(dialog).toContainText('no longer have access to ACM0000001');
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(coding).toContainText('Not saved: this change would end your access');
  await expect(page.locator('.review__position')).toHaveText('Doc 1 of 250');
  expect(mock.coding.saves.map((s) => s.confirmAccessLoss)).toEqual([false]);

  // Confirmed (the API refuses the plain save again first): saved with confirmAccessLoss, and Review moves on.
  await page.keyboard.press('Control+Enter');
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Save and lose access' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('.review__position')).toHaveText('Doc 2 of 250');
  await expect(page.locator('.review__bar')).toContainText('ACM0000002');
  expect(mock.coding.saves.map((s) => s.confirmAccessLoss)).toEqual([false, false, true]);
  await page.waitForLoadState('networkidle');
  const afterSave = mock.documentRequests(1);

  // Previous: the no-access state, with no request for it.
  await page.keyboard.press('[');
  await expect(
    page.getByRole('region', { name: 'Viewer' }).getByRole('heading', {
      name: 'Document not available',
    }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(listRow(page, 0)).toContainText('No longer available');
  await page.waitForLoadState('networkidle');
  expect(mock.documentRequests(1)).toEqual(afterSave);
});

test('a document hidden while open leaves the screen on its next request, and Withhold without a Basis is refused inline', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await openRow(page, 0);
  const viewer = page.getByRole('region', { name: 'Viewer' });
  const coding = page.getByRole('region', { name: 'Coding' });
  await expect(viewer.getByLabel('Extracted text of ACM0000001')).toBeVisible();

  // E13-T01 through the API: Withhold without a Privilege Basis is refused, the message under the field.
  await coding.getByRole('radio', { name: 'Responsive', exact: true }).check();
  await coding.getByRole('radio', { name: 'Withhold', exact: true }).check();
  await page.keyboard.press('Control+KeyS');
  const basis = coding.getByRole('group', { name: /Privilege Basis/ });
  await expect(basis).toContainText(
    'Privilege Basis is required when Privilege is Withhold or Redact.',
  );
  await expect(basis).toHaveAttribute('aria-invalid', 'true');
  await expect(coding.getByRole('checkbox', { name: 'Attorney-Client' })).toBeFocused();

  // Hidden by someone else while open: the next save answers 404, and the document leaves the screen.
  mock.revokeAccess(1);
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Control+KeyS');
  await expect(viewer.getByRole('heading', { name: 'Document not available' })).toBeVisible();
  await expect(viewer.getByLabel('Extracted text of ACM0000001')).toHaveCount(0);
  await expect(coding).toContainText('No coding to show');
  await page.waitForLoadState('networkidle');
  const requests = mock.documentRequests(1);
  await page.keyboard.press('Escape');
  await expect(listRow(page, 0)).toContainText('No longer available');
  expect(mock.documentRequests(1)).toEqual(requests);
});

test('keyboard path: the confirmation, the no-access state and the placeholder row', async ({
  page,
  mock,
}) => {
  mock.coding.loseAccessWhen(1005, 42);
  await openPage(page, '/w/ws-1/documents');
  await openRow(page, 0);
  const coding = page.getByRole('region', { name: 'Coding' });
  await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();
  // Alt+Shift+C, then 1 / 4 jump to the fields; digits choose.
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit3');
  await page.keyboard.press('Digit2');
  await expect(coding.getByRole('radio', { name: 'CONFIDENTIAL', exact: true })).toBeChecked();
  await tabTo(page, coding.getByRole('button', { name: 'Save', exact: true }), 12);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('alertdialog', { name: 'Save and lose access?' });
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await tabTo(page, dialog.getByRole('button', { name: 'Save and lose access' }), 3);
  await page.keyboard.press('Enter');
  // A plain Save that ends access also moves on.
  await expect(page.locator('.review__position')).toHaveText('Doc 2 of 250');
  await page.keyboard.press('[');
  await expect(page.locator('.review__position')).toHaveText('Doc 1 of 250');
  await expect(page.getByRole('heading', { name: 'Document not available' })).toBeVisible();
  // The pane cycle still reaches every region of the no-access state.
  await page.keyboard.press('Alt+Shift+KeyG');
  await page.keyboard.press('Escape');
  await expect(listRow(page, 0)).toContainText('No longer available');
  await expect(page.getByRole('grid', { name: 'Documents' })).toBeFocused();
});

for (const theme of THEMES) {
  test.describe(`axe (${theme})`, () => {
    test.beforeEach(async ({ page }) => useTheme(page, theme));

    test('no-access state, placeholder row, access note and the Save and lose access dialog', async ({
      page,
      mock,
    }, testInfo) => {
      mock.coding.loseAccessWhen(1005, 42);
      await openPage(page, '/w/ws-1/documents');
      await openRow(page, 0);
      const coding = page.getByRole('region', { name: 'Coding' });
      await coding.getByRole('radio', { name: 'Responsive', exact: true }).check();
      await coding.getByRole('radio', { name: 'CONFIDENTIAL', exact: true }).check();
      await expect(coding).toContainText('affect who may see this document');
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.keyboard.press('Control+Enter');
      const dialog = page.getByRole('alertdialog', { name: 'Save and lose access?' });
      await expect(dialog).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await dialog.getByRole('button', { name: 'Save and lose access' }).click();
      await expect(page.locator('.review__position')).toHaveText('Doc 2 of 250');
      await page.keyboard.press('[');
      await expect(page.getByRole('heading', { name: 'Document not available' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.keyboard.press('Escape');
      await expect(listRow(page, 0)).toContainText('No longer available');
      await expectNoSeriousAxeViolations(page, testInfo);
    });
  });
}
