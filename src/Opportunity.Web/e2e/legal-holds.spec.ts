import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Legal holds (E20-T01) in Admin › Workspace Settings with the keyboard only: place a hold, see it in the header and
// in the blocked deletion, request its release; then, as the second person, approve a release someone else requested.

test('places a legal hold, sees deletion blocked and requests the release with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/settings');
  const card = page.getByRole('region', { name: 'Legal hold' });
  await expect(card.getByText('No legal hold')).toBeVisible();
  const header = page.getByRole('banner');
  await expect(header.getByText('Legal hold')).toHaveCount(0);

  await tabTo(page, card.getByRole('button', { name: 'Place legal hold…' }), 80);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Place legal hold' });
  const reason = dialog.getByRole('textbox', { name: 'Reason for the hold' });
  await expect(reason).toBeFocused();

  // The reason is required: submitting without it keeps the dialog open and returns focus to the field.
  await tabTo(page, dialog.getByRole('button', { name: 'Place hold' }));
  await page.keyboard.press('Enter');
  await expect(dialog.getByText('Enter a reason.')).toBeVisible();
  await expect(reason).toBeFocused();

  await page.keyboard.type('Complaint served; preserve all custodians');
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('textbox', { name: 'Matter or notice reference' })).toBeFocused();
  await page.keyboard.type('2026-CV-0142');
  await page.keyboard.press('Tab');
  const approval = dialog.getByRole('checkbox', {
    name: 'Releasing needs approval by a second person',
  });
  await expect(approval).toBeFocused();
  await expect(approval).toBeChecked();
  await tabTo(page, dialog.getByRole('button', { name: 'Place hold' }));
  await page.keyboard.press('Enter');

  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Legal hold placed.').first()).toBeVisible();
  expect(mock.legalHolds.writes[0].body).toEqual({
    reason: 'Complaint served; preserve all custodians',
    matterReference: '2026-CV-0142',
    releaseRequiresApproval: true,
  });
  await expect(card.getByText('On legal hold')).toBeVisible();
  await expect(card.getByText('Complaint served; preserve all custodians')).toBeVisible();
  await expect(header.getByText('Legal hold')).toBeVisible();

  // Deletion now explains the hold.
  const remove = page.getByRole('button', { name: 'Delete workspace…' });
  await expect(remove).toHaveAccessibleDescription(/legal hold/);
  await tabTo(page, remove, 80);
  await page.keyboard.press('Enter');
  const blocked = page.getByRole('alertdialog', { name: 'Deletion blocked' });
  await expect(blocked.getByText('Complaint served; preserve all custodians')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(blocked).toHaveCount(0);
  await expect(remove).toBeFocused();

  // Releasing it is a request for a second person; the requester gets no approve button.
  await tabTo(page, card.getByRole('button', { name: 'Release…' }), 80, { backwards: true });
  await page.keyboard.press('Enter');
  const release = page.getByRole('dialog', { name: 'Release legal hold' });
  await expect(release.getByRole('textbox', { name: 'Reason for releasing' })).toBeFocused();
  await page.keyboard.type('Case dismissed with prejudice');
  await tabTo(page, release.getByRole('button', { name: 'Request release' }));
  await page.keyboard.press('Enter');
  await expect(release).toHaveCount(0);
  await expect(card.getByText('Release requested by you')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Approve release…' })).toHaveCount(0);
  expect(mock.legalHolds.writes[1]).toMatchObject({
    body: { reason: 'Case dismissed with prejudice' },
    ifMatch: '"1"',
  });
  await expect(header.getByText('Legal hold')).toBeVisible();
});

test('a second person approves a release someone else requested with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-2/admin/settings');
  const header = page.getByRole('banner');
  await expect(header.getByText('Legal hold')).toBeVisible();
  const card = page.getByRole('region', { name: 'Legal hold' });
  await expect(card.getByText('Release requested by Dana Counsel')).toBeVisible();

  await tabTo(page, card.getByRole('button', { name: 'Approve release…' }), 80);
  await page.keyboard.press('Enter');
  const confirm = page.getByRole('alertdialog', { name: 'Approve release' });
  await expect(confirm).toBeVisible();
  await tabTo(page, confirm.getByRole('button', { name: 'Approve release' }));
  await page.keyboard.press('Enter');

  await expect(page.getByText('Legal hold released.').first()).toBeVisible();
  await expect(card.getByText('No legal hold')).toBeVisible();
  await expect(card.getByText('2 released holds')).toBeVisible();
  await expect(header.getByText('Legal hold')).toHaveCount(0);
  expect(mock.legalHolds.writes[0]).toMatchObject({
    path: '/api/v1/workspaces/ws-2/preservation-locks/hold-2/release/approve',
    ifMatch: '"2"',
  });
});
