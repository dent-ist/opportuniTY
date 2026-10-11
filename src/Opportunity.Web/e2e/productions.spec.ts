import { expect, openPage, test } from './support/fixtures';
import { tabTo, waitForRouteFocus } from './support/tab';

// Exports and Productions (E12-T08) against the mock (e2e/support/mock-productions.ts): New Export with the keyboard
// only (frozen set ID and count before the run, gateway downloads after), a production's QC gate with an override and
// an acknowledged warning, the output of a finalized production with a re-run of its volume, and "Open in Documents".

test('runs New Export with the keyboard and shows the frozen set and the gateway downloads', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/exports');
  await expect(page.getByRole('row')).toHaveCount(2);
  await expect(page.getByRole('row').nth(1)).toContainText('snap-exp-1');
  await expect(page.getByRole('row').nth(1)).toContainText('120');

  await page.getByRole('link', { name: 'New Export' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'New Export' })).toBeVisible();
  await waitForRouteFocus(page);
  const freeze = page.getByRole('button', { name: 'Freeze set' });
  await tabTo(page, freeze, 40);
  await page.keyboard.press('Enter');
  const frozen = page.getByRole('status', { name: 'Frozen set' });
  await expect(frozen).toContainText('snapshot-1');
  await expect(frozen).toContainText('Documents');
  expect(mock.snapshots[0].body).toMatchObject({ purpose: 'export', expand: { family: true } });

  for (const step of ['Volume & folders', 'Files', 'Load file', 'Review & run']) {
    await tabTo(page, page.getByRole('button', { name: 'Continue' }), 60);
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 2, name: new RegExp(step) })).toBeFocused();
  }
  await expect(page.getByLabel('Export summary')).toContainText('snapshot-1');
  await tabTo(page, page.getByRole('button', { name: 'Start export' }), 40);
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/w\/ws-1\/exports\/exp-3$/);
  const created = mock.productions.requests.find((r) => r.method === 'POST');
  expect(created?.body).toMatchObject({ snapshotId: 'snapshot-1', delimiters: 'concordance' });
  expect(created?.idempotencyKey).toBeTruthy();
  await expect(page.getByRole('link', { name: 'Download package (ZIP)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/exports/exp-3/package',
  );
  await expect(page.getByRole('link', { name: 'Download manifest' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/exports/exp-3/files/ef-1/content',
  );
});

test('finalizes a production behind the QC gate with an override and an acknowledged warning', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/productions');
  const draft = page.getByRole('row', { name: /Volume 2/ });
  await expect(draft).toContainText('ACM0000121 – ACM0000180');
  await expect(draft).toContainText('Bates assigned');

  await openPage(page, '/w/ws-1/productions/prod-2?tab=qc');
  await expect(page.getByLabel('Production summary')).toContainText('snap-prod-2');
  await expect(page.getByLabel('Production summary')).toContainText('12');
  await tabTo(page, page.getByRole('button', { name: 'Run QC checks' }), 60);
  await page.keyboard.press('Enter');
  await expect(page.getByText('Pages that could not be rendered', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'QC report (CSV)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/productions/prod-2/qc/report?format=csv',
  );

  await page.getByRole('button', { name: 'Show documents' }).first().click();
  await expect(page.getByText('Page 2 could not be rendered')).toBeVisible();

  const override = page.getByRole('checkbox', { name: /Override/ });
  await tabTo(page, override, 40);
  await page.keyboard.press('Space');
  await page
    .getByLabel('Reason for overriding Pages that could not be rendered')
    .fill('Spreadsheets are produced natively');
  const ack = page.getByRole('checkbox', { name: /I have reviewed the 1 warning/ });
  await tabTo(page, ack, 10);
  await page.keyboard.press('Space');
  await tabTo(page, page.getByRole('button', { name: 'Finalize production' }), 10);
  await page.keyboard.press('Enter');
  await page.getByRole('alertdialog').getByRole('button', { name: 'Finalize' }).click();

  await expect(
    page.locator('.toast__message').filter({ hasText: 'Volume 2 is finalized.' }),
  ).toBeVisible();
  const finalize = mock.productions.requests.find((r) => r.path.endsWith('/finalize'));
  expect(finalize?.ifMatch).toBe('"1"');
  expect(finalize?.body).toEqual({
    acknowledgeWarnings: true,
    qcOverrides: [{ check: 'renderFailure', reason: 'Spreadsheets are produced natively' }],
  });
  await expect(page.getByText('Override reason: Spreadsheets are produced natively')).toBeVisible();
});

test('shows the output of a finalized production and re-runs its volume with the same specification', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/productions/prod-1?tab=output');
  await expect(page.getByText('Identical to the first run')).toHaveCount(2);
  await expect(page.getByRole('link', { name: 'Download volume (ZIP)' }).first()).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/productions/prod-1/volumes/vol-2/package',
  );
  await page.getByRole('button', { name: 'Verify against manifest' }).click();
  await expect(page.getByText('match the manifest')).toBeVisible();

  await page.getByRole('button', { name: 'Re-run with same specification' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Re-run' }).click();
  await expect(page.getByText('Identical to the first run')).toHaveCount(3);
  const started = mock.productions.requests.filter(
    (r) => r.method === 'POST' && r.path.endsWith('/volumes'),
  );
  expect(started).toHaveLength(1);
  expect(started[0].idempotencyKey).toBeTruthy();

  // Copy settings starts a new draft that continues the numbering after every range of the prefix.
  await page.getByRole('link', { name: 'Copy settings' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'New Production' })).toBeVisible();
  await expect(page.getByLabel('Start number')).toHaveValue('181');
});

test('opens a production’s documents in Documents', async ({ page }) => {
  await openPage(page, '/w/ws-1/productions/prod-2?tab=documents');
  await expect(page.getByRole('region', { name: 'Production documents' })).toContainText(
    'ACM0000121',
  );
  await page.getByRole('link', { name: 'Open listed documents in Documents' }).click();
  await expect(page).toHaveURL(/\/w\/ws-1\/documents$/);
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Showing: Documents of Volume 2.' }),
  ).toBeVisible();
});
