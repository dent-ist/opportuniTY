import type { Page } from '@playwright/test';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Families and duplicates (E16-T10): the family-grouped list (a tree grid), Related Items in Review mode, Apply to
// Family with its conflict preview, the duplicate pivot, and propagation above the threshold as a job. The mock
// (./support/mock-relationships.ts) follows the wave-12 shared contract: ACM n % 4 === 3 is the parent of n + 1,
// ACM n % 10 === 5 and 6 are duplicates. Hidden members are omitted entirely (Q-52).

const row = (page: Page, controlNumber: string) =>
  page.getByRole('treegrid', { name: 'Documents' }).getByRole('row', { name: controlNumber });

/** Presses `key` and waits until the active cell of the list moved. */
async function step(page: Page, key: string, times = 1): Promise<void> {
  const list = page.locator('#grid-ws-1');
  for (let i = 0; i < times; i++) {
    const before = await list.getAttribute('aria-activedescendant');
    await page.keyboard.press(key);
    await expect
      .poll(() => list.getAttribute('aria-activedescendant'), { timeout: 2_000 })
      .not.toBe(before)
      .catch(() => undefined);
  }
}

test('groups families, codes a parent and applies it to the family after a conflict preview, keyboard only', async ({
  page,
  mock,
}) => {
  test.setTimeout(90_000);
  await openPage(page, '/w/ws-1/documents');
  await expect(page.getByRole('grid', { name: 'Documents' })).toBeVisible();

  // Group families: sorted by Family Date, attachments one level below their parent.
  await tabTo(page, page.getByRole('button', { name: 'Group families' }), 60);
  await page.keyboard.press('Enter');
  const tree = page.getByRole('treegrid', { name: 'Documents' });
  await expect(tree).toBeVisible();
  await expect(page.getByRole('button', { name: 'Group families' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  expect(mock.lastSearch()?.['sort']).toEqual([{ field: 'familyDate', direction: 'asc' }]);
  await expect(row(page, /ACM0000003/)).toHaveAttribute('aria-level', '1');
  await expect(row(page, /ACM0000003/)).toHaveAttribute('aria-expanded', 'true');
  await expect(row(page, /ACM0000004/)).toHaveAttribute('aria-level', '2');

  // Collapse and expand the family of ACM0000003 from its Family column (Left / Right).
  await tree.focus();
  await step(page, 'ArrowRight'); // Control Number → Family
  await step(page, 'ArrowDown', 2);
  await page.keyboard.press('ArrowLeft');
  await expect(row(page, /ACM0000003/)).toHaveAttribute('aria-expanded', 'false');
  await expect(row(page, /ACM0000004/)).toHaveCount(0);
  await page.keyboard.press('ArrowRight');
  await expect(row(page, /ACM0000004/)).toHaveAttribute('aria-level', '2');

  // Open ACM0000011 (parent of ACM0000012, which is coded Responsive) and code it Not Responsive.
  await step(page, 'ArrowDown', 8);
  await page.keyboard.press('Enter');
  await expect(page.locator('.review__identity')).toContainText('ACM0000011');
  const related = page.getByRole('region', { name: 'Related Items' });
  await expect(related.getByRole('tab', { name: /Family/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(related.getByRole('row', { name: /ACM0000012/ })).toContainText('Responsive');
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit2'); // Not Responsive
  await page.keyboard.press('Control+KeyS');
  await expect(page.getByRole('region', { name: 'Coding' })).toContainText('Saved');
  expect(mock.relationships.requests).toEqual([]); // nothing propagates on save (Q-14)

  // Apply to Family… (Alt+Shift+F): the saved field is ticked; preview, then apply.
  await page.keyboard.press('Alt+Shift+KeyF');
  const dialog = page.getByRole('dialog', { name: 'Apply to Family' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('checkbox', { name: /Responsiveness/ })).toBeChecked();
  await tabTo(page, dialog.getByRole('button', { name: 'Preview' }), 20);
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('status')).toContainText(
    'Applies to 1 document. 1 is already coded differently',
  );
  await expect(dialog.getByRole('row', { name: /ACM0000012/ })).toContainText(
    'ResponsivenessResponsiveNot Responsive',
  );
  await tabTo(page, dialog.getByRole('button', { name: 'Apply to 1 document' }), 20);
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Coding applied to 1 document.' }),
  ).toBeVisible();
  await expect(related.getByRole('row', { name: /ACM0000012/ })).toContainText('Not Responsive');
  const apply = mock.relationships.requests.find((r) => r.kind === 'apply');
  expect(apply?.body).toEqual({ previewId: 'preview-1' });
  expect(apply?.idempotencyKey).toBeTruthy();
});

test('Related Items: only visible members, opening a member keeps the cursor, the duplicate pivot', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  // The email thread is a column the Columns dialog offers (the workspace's thread field).
  await page.getByRole('button', { name: 'Columns', exact: true }).click();
  const chooser = page.getByRole('dialog', { name: 'Columns and sort' });
  await expect(chooser.getByRole('button', { name: 'Email Thread Group' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(chooser).toBeHidden();

  await page
    .getByRole('grid', { name: 'Documents' })
    .getByRole('row', { name: /ACM0000003/ })
    .dblclick();
  const related = page.getByRole('region', { name: 'Related Items' });
  await expect(related.getByRole('row')).toHaveCount(3); // header + ACM3 + ACM4
  await expect(related).not.toContainText(/restricted|hidden/i);

  await related.getByRole('button', { name: 'ACM0000004' }).click();
  await expect(page.locator('.review__position')).toHaveText('Doc 3 of 250');
  await expect(page.getByText('Viewing related item ACM0000004')).toBeVisible();

  // The grid's duplicate indicator: one click lists the duplicate group.
  await page.getByRole('button', { name: 'Back to list' }).click();
  const list = page.getByRole('grid', { name: 'Documents' });
  await list
    .getByRole('row', { name: /ACM0000005/ })
    .getByRole('button', { name: 'Has duplicates' })
    .click();
  await expect.poll(() => mock.lastSearch()?.['query']).toBe('duplicategroup:"dup-5"');
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Showing: Duplicates of ACM0000005.' }),
  ).toBeVisible();
});

test.describe('above the threshold', () => {
  test.use({ api: { propagationThreshold: 0 } });

  test('propagation runs as a Mass Edit job linked to the job monitor', async ({ page }) => {
    await openPage(page, '/w/ws-1/documents');
    await page
      .getByRole('grid', { name: 'Documents' })
      .getByRole('row', { name: /ACM0000003/ })
      .dblclick();
    await page.getByRole('button', { name: 'Apply to Family…' }).click();
    const dialog = page.getByRole('dialog', { name: 'Apply to Family' });
    await dialog.getByRole('checkbox', { name: /Responsiveness/ }).check();
    await dialog.getByRole('button', { name: 'Preview' }).click();
    await expect(dialog.getByText('this runs as a Mass Edit job')).toBeVisible();
    await dialog.getByRole('button', { name: 'Start job for 1 document' }).click();
    await expect(dialog.getByText('running as a Mass Edit job')).toBeVisible();
    await expect(dialog.getByRole('link', { name: 'Open job' })).toHaveAttribute(
      'href',
      '/w/ws-1/jobs/job-propagation-1',
    );
  });
});
