import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test } from './support/fixtures';
import { tabTo, waitForRouteFocus } from './support/tab';

// Workspace deletion (E20-T02) with the keyboard only: a Workspace Admin requests the deletion of ws-1 from
// Admin › Workspace Settings; a Retention Approver approves a request someone else made and downloads the destruction
// certificate of a finished deletion.

const APPROVER = ['Installation.ManageWorkspaces', 'Installation.ApproveDeletion'];

test('requests the deletion of a workspace with the keyboard only', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/admin/settings');
  const card = page.getByRole('region', { name: 'Delete workspace' });
  const remove = card.getByRole('button', { name: 'Delete workspace…' });
  await expect(remove).toBeEnabled();

  await tabTo(page, remove, 80);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('alertdialog', { name: 'Delete Acme v. Widget' });
  const keep = dialog.getByRole('radio', { name: /Keep productions/ });
  await expect(keep).toBeFocused();
  await expect(keep).toBeChecked();
  await expectNoSeriousAxeViolations(page, test.info());

  // Arrow keys change what is kept; the summary follows.
  await page.keyboard.press('ArrowDown');
  await expect(dialog.getByRole('radio', { name: /Remove everything/ })).toBeChecked();
  await expect(dialog.getByRole('region', { name: 'Kept' })).not.toContainText('Productions');
  await page.keyboard.press('ArrowUp');
  await expect(keep).toBeChecked();

  // The request needs the exact name before it can be sent, and a reason.
  const submit = dialog.getByRole('button', { name: 'Request deletion' });
  await expect(submit).toBeDisabled();
  await tabTo(page, dialog.getByRole('textbox', { name: 'Reason for deleting' }));
  await page.keyboard.type('Matter closed; the protective order requires destruction');
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('textbox', { name: 'Order or agreement reference' })).toBeFocused();
  await page.keyboard.type('PO ¶ 14');
  await tabTo(page, dialog.getByRole('textbox', { name: /Type Acme v\. Widget to confirm/ }));
  await page.keyboard.type('Acme v. Widget');
  await expect(submit).toBeEnabled();
  await page.keyboard.press('Enter');

  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Deletion requested.').first()).toBeVisible();
  expect(mock.deletions.writes[0]).toMatchObject({
    path: '/api/v1/workspaces/ws-1/deletions',
    body: {
      retentionProfile: 'retainRecords',
      reason: 'Matter closed; the protective order requires destruction',
      externalReference: 'PO ¶ 14',
      confirmName: 'Acme v. Widget',
    },
  });
  await expect(card).toContainText('waiting for approval by a second person');
  await expect(card.getByRole('button', { name: 'Delete workspace…' })).toHaveCount(0);

  // The link leads to the request, where the requester may cancel but not approve.
  await tabTo(page, card.getByRole('link', { name: /View the deletion request/ }), 80);
  await page.keyboard.press('Enter');
  await waitForRouteFocus(page);
  await expect(
    page.getByRole('heading', { level: 1, name: 'Delete Acme v. Widget' }),
  ).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve deletion…' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Cancel request…' })).toBeVisible();
});

test.describe('as a Retention Approver', () => {
  test.use({ api: { installationPermissions: APPROVER } });

  test('approves a request someone else made with the keyboard only', async ({ page, mock }) => {
    await openPage(page, '/workspaces');
    await tabTo(page, page.getByRole('link', { name: 'Workspace deletions' }), 60);
    await page.keyboard.press('Enter');
    await waitForRouteFocus(page);
    await expect(page.getByText('1 waiting for your approval')).toBeVisible();

    await tabTo(page, page.getByRole('link', { name: 'Delta Matter' }), 40);
    await page.keyboard.press('Enter');
    await waitForRouteFocus(page);
    await expect(
      page.getByRole('heading', { level: 1, name: 'Delete Delta Matter' }),
    ).toBeVisible();
    await expect(page.getByText('Dana Counsel').first()).toBeVisible();

    await tabTo(page, page.getByRole('textbox', { name: 'Approval note' }), 40);
    await page.keyboard.type('Order of 2026-10-01 confirmed');
    await tabTo(page, page.getByRole('button', { name: 'Approve deletion…' }));
    await page.keyboard.press('Enter');
    const confirm = page.getByRole('alertdialog', { name: 'Approve deleting Delta Matter?' });
    await expect(confirm).toBeVisible();
    await tabTo(page, confirm.getByRole('button', { name: 'Approve deletion' }));
    await page.keyboard.press('Enter');

    await expect(confirm).toHaveCount(0);
    await expect(page.getByText('Deletion approved.').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Approve deletion…' })).toHaveCount(0);
    await expect(page.getByText('Order of 2026-10-01 confirmed')).toBeVisible();
    expect(mock.deletions.writes[0]).toMatchObject({
      path: '/api/v1/workspace-deletions/del-7/approve',
      body: { note: 'Order of 2026-10-01 confirmed' },
      ifMatch: '"1"',
    });
  });

  test('shows the finished run and downloads the destruction certificate', async ({ page }) => {
    await openPage(page, '/workspace-deletions/del-5');
    const progress = page.getByRole('region', { name: 'Progress' });
    await expect(progress.getByRole('listitem')).toHaveCount(9);
    await expect(progress).toContainText('48,210 records removed');

    const certificate = page.getByRole('link', { name: 'Download destruction certificate' });
    await tabTo(page, certificate, 40);
    const download = page.waitForEvent('download');
    await page.keyboard.press('Enter');
    // The browser fetches the attachment itself (outside the page's request routing); the API names the file.
    expect((await download).url()).toMatch(/\/api\/v1\/workspace-deletions\/del-5\/certificate$/);
  });
});
