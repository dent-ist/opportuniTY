import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Review Batches (#178) against the mock (e2e/support/mock-review-batches.ts): a reviewer checks out the next batch
// with the keyboard only, reviews it in Review mode (Save & Next stays inside the batch) and checks it in at its end;
// a manager releases and reassigns batches; New Batch Set freezes a saved search and creates the set.

test('checks out the next batch, reviews only its documents and checks it in, keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/review-batches');
  await expect(page.getByRole('heading', { level: 1, name: 'Review Batches' })).toBeVisible();
  const sets = page.getByRole('region', { name: 'Batch Sets' });
  await expect(sets.getByRole('row').nth(1)).toContainText('Smith first pass');

  await tabTo(page, page.getByRole('button', { name: 'Check out next batch of Smith first pass' }));
  await page.keyboard.press('Enter');

  // Documents, bound to the batch, opens its first document in Review mode.
  await expect(page).toHaveURL(/\/w\/ws-1\/documents\?batch=rb-1/);
  await expect(page.locator('.review__position')).toHaveText('Doc 1 of 5');
  await expect(page.getByRole('group', { name: 'Review batch' })).toContainText('FP_0001');
  expect(mock.lastSearch()).toEqual(expect.objectContaining({ reviewBatchId: 'rb-1' }));

  for (let i = 0; i < 4; i++) await page.keyboard.press('Control+Enter');
  await expect(page.locator('.review__position')).toHaveText('Doc 5 of 5');
  await page.keyboard.press('BracketRight');
  await expect(
    page.getByText('End of batch FP_0001. There are no more documents in it.'),
  ).toBeVisible();

  await tabTo(page, page.getByRole('button', { name: 'Check in batch…' }), 60);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Check in FP_0001' });
  await expect(dialog.getByRole('button', { name: 'Mark completed' })).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/w\/ws-1\/review-batches$/);
  const checkIn = mock.reviewBatches.requests.find((r) => r.path.endsWith('/check-in'));
  expect(checkIn?.body).toEqual({ completed: true });
  expect(checkIn?.ifMatch).toBe('"2"');
  await expect(page.getByRole('heading', { level: 1, name: 'Review Batches' })).toBeVisible();
  await expect(page.getByText('You hold no batch.')).toBeVisible();
});

test('a manager releases and reassigns batches of a set', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/review-batches/set-1');
  const batches = page.getByRole('region', { name: 'Batches' });
  await expect(batches.getByRole('row', { name: /FP_0003/ })).toContainText('Completed');

  await batches.getByRole('button', { name: 'Assign FP_0003' }).click();
  const dialog = page.getByRole('dialog', { name: 'Assign FP_0003' });
  await dialog.getByRole('combobox', { name: 'Reviewer' }).selectOption({ label: 'Alex Reviewer' });
  await dialog.getByRole('button', { name: 'Assign' }).click();
  await expect(batches.getByRole('row', { name: /FP_0003/ })).toContainText('In progress');
  await expect(batches.getByRole('row', { name: /FP_0003/ })).toContainText('You');

  await batches.getByRole('button', { name: 'Release FP_0003' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Release batch' }).click();
  await expect(batches.getByRole('row', { name: /FP_0003/ })).toContainText('Pending');
  const writes = mock.reviewBatches.requests.filter((r) => r.method === 'PUT');
  expect(writes.map((w) => w.body)).toEqual([{ assigneeId: 'user-1' }, { assigneeId: null }]);
});

test('New Batch Set freezes a saved search and cuts it by Family Date', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/review-batches/new');
  await page.getByRole('textbox', { name: 'Batch Set name' }).fill('Jones first pass');
  await page.getByRole('button', { name: 'Freeze set' }).click();
  await expect(page.getByRole('status', { name: 'Frozen set' })).toBeVisible();
  await page.getByRole('textbox', { name: 'Batch name prefix' }).fill('JONES');
  await page.getByRole('textbox', { name: 'Maximum batch size' }).fill('250');
  await page.getByRole('combobox', { name: 'Order' }).selectOption('familyDate');
  await page.getByRole('button', { name: 'Create Batch Set' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Jones first pass' })).toBeVisible();
  const created = mock.reviewBatches.requests.find((r) => r.method === 'POST');
  expect(created?.body).toEqual(
    expect.objectContaining({
      batchPrefix: 'JONES',
      maxBatchSize: 250,
      keepFamiliesTogether: true,
      sort: 'familyDate',
    }),
  );
  expect(mock.snapshots.at(-1)?.body).toEqual(expect.objectContaining({ purpose: 'reviewBatch' }));
});

test.describe('without Coding.Write', () => {
  test.use({ api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'Coding.Write') } });

  test('Review Batches is not in the navigation', async ({ page }) => {
    await openPage(page, '/w/ws-1/documents');
    await expect(
      page.getByRole('navigation', { name: 'Workspace sections' }).getByRole('link', {
        name: 'Review Batches',
      }),
    ).toHaveCount(0);
  });
});
