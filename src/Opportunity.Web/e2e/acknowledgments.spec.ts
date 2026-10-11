import { expect, openPage, test } from './support/fixtures';
import { tabTo, waitForRouteFocus } from './support/tab';

// Reviewer attestation and protective-order acknowledgment (E20-T03) with the keyboard only: a member who has not
// accepted is sent to the text before any workspace content, reads it, confirms and continues where they were going;
// an administrator publishes a new version and accepts it like everyone else.

test.describe('a member who has not accepted', () => {
  test.use({ api: { acknowledgmentPending: true } });

  test('reads and accepts the text with the keyboard only, then continues to the page they asked for', async ({
    page,
    mock,
  }) => {
    await openPage(page, '/w/ws-1/jobs');
    await expect(page).toHaveURL('/w/ws-1/acknowledgment?returnUrl=%2Fw%2Fws-1%2Fjobs');
    await expect(
      page.getByRole('heading', { level: 1, name: 'Protective order acknowledgment (Exhibit A)' }),
    ).toBeVisible();
    // The workspace stays closed until then: no sections, no job tray.
    await expect(page.getByRole('navigation', { name: 'Workspace sections' })).toHaveCount(0);

    // The text region is reachable and scrollable with the keyboard.
    const textRegion = page.getByRole('region', { name: 'Acknowledgment text' });
    await tabTo(page, textRegion);
    await expect(textRegion).toContainText('agree to be bound by it');

    // Accepting without the confirmation explains and returns focus to the box.
    await tabTo(page, page.getByRole('button', { name: 'Accept and continue' }));
    await page.keyboard.press('Enter');
    await expect(page.getByText('Tick the box to confirm you agree.')).toBeVisible();
    const confirm = page.getByRole('checkbox', {
      name: 'I have read this text and agree to be bound by it',
    });
    await expect(confirm).toBeFocused();
    expect(mock.acknowledgments.writes).toEqual([]);

    await page.keyboard.press('Space');
    await expect(confirm).toBeChecked();
    await tabTo(page, page.getByRole('button', { name: 'Accept and continue' }));
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL('/w/ws-1/jobs');
    await expect(page.getByText('Acknowledgment recorded.').first()).toBeVisible();
    await expect(page.getByRole('navigation', { name: 'Workspace sections' })).toBeVisible();
    expect(mock.acknowledgments.writes).toHaveLength(1);
    expect(mock.acknowledgments.writes[0].body).toMatchObject({ version: 1 });
    expect(String(mock.acknowledgments.writes[0].body['textSha256'])).toMatch(/^[0-9a-f]{64}$/);
  });
});

test('an administrator publishes a new version with the keyboard only and accepts it too', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/acknowledgments');
  await expect(page.getByRole('heading', { level: 2, name: 'Who accepted' })).toBeVisible();
  await expect(page.getByRole('row', { name: /Parker Pending/ })).toContainText('Not accepted');
  await expect(page.getByRole('link', { name: 'Download roster (CSV)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/acknowledgment-roster/export',
  );

  await tabTo(page, page.getByRole('button', { name: 'Publish new version…' }), 80);
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Publish version 2' });
  const title = dialog.getByRole('textbox', { name: /Title/ });
  await expect(title).toBeFocused();
  await expect(title).toHaveValue('Protective order acknowledgment (Exhibit A)');
  await page.keyboard.press('Tab');
  const text = dialog.getByRole('textbox', { name: /Text members accept/ });
  await expect(text).toBeFocused();
  await page.keyboard.press('ControlOrMeta+End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('I confirm that I have no conflict of interest in this matter.');
  await tabTo(page, dialog.getByRole('button', { name: 'Publish version 2' }));
  await page.keyboard.press('Enter');

  await expect(dialog).toHaveCount(0);
  expect(mock.acknowledgments.writes[0]).toMatchObject({
    path: '/api/v1/workspaces/ws-1/acknowledgment-versions',
    ifMatch: '"1"',
  });
  expect(String(mock.acknowledgments.writes[0].body['text'])).toContain('no conflict of interest');

  // The publisher, like every member, accepts the new version before going on.
  await expect(page).toHaveURL(
    '/w/ws-1/acknowledgment?returnUrl=%2Fw%2Fws-1%2Fadmin%2Facknowledgments',
  );
  await expect(page.getByText('Version 2 · published')).toBeVisible();
  await waitForRouteFocus(page);
  await tabTo(page, page.getByRole('checkbox', { name: /agree to be bound/ }));
  await page.keyboard.press('Space');
  await tabTo(page, page.getByRole('button', { name: 'Accept and continue' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL('/w/ws-1/admin/acknowledgments');
  await expect(page.getByText('version 2, published by Alex Reviewer')).toBeVisible();
});
