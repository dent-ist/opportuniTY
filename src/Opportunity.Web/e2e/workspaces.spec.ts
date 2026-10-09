import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Workspace management (E04-T07) with the keyboard only: create a workspace, land on its setup checklist, change its
// settings. The mouse is never used.

test('creates a workspace, lands on its setup checklist and edits its settings with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/workspaces');
  await tabTo(page, page.getByRole('button', { name: 'New workspace' }));
  await page.keyboard.press('Enter');

  const dialog = page.getByRole('dialog', { name: 'New workspace' });
  await expect(dialog).toBeVisible();
  const name = dialog.getByRole('textbox', { name: 'Workspace name' });
  await expect(name).toBeFocused();

  // Submitting without a name keeps the dialog open and returns focus to the field with the message.
  await tabTo(page, dialog.getByRole('button', { name: 'Create workspace' }));
  await page.keyboard.press('Enter');
  await expect(dialog.getByText('Enter a workspace name.')).toBeVisible();
  await expect(name).toBeFocused();

  await page.keyboard.type('Gamma Matter');
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('textbox', { name: 'Matter number' })).toBeFocused();
  await page.keyboard.type('2026-07');
  await tabTo(page, dialog.getByRole('button', { name: 'Create workspace' }));
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/w\/ws-new-1\/admin\/setup$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Set up Gamma Matter' })).toBeFocused();
  await expect(page).toHaveTitle('Setup Checklist · Gamma Matter · opportuniTY');
  expect(mock.workspaceWrites[0].body).toMatchObject({
    name: 'Gamma Matter',
    matterNumber: '2026-07',
  });

  // The new workspace is empty: every step is still to do, and only Import has a page today.
  const steps = page.locator('main ol > li');
  await expect(steps).toHaveCount(4);
  await expect(page.getByText('0 of 4 steps done')).toBeVisible();
  await expect(steps.nth(0)).toContainText('No imports yet');
  await expect(steps.nth(1)).toContainText('Page coming soon');
  await tabTo(page, page.getByRole('link', { name: 'Start a new import' }));

  const areas = page.getByRole('navigation', { name: 'Admin' });
  await expect(areas.getByRole('link', { name: 'Setup Checklist' })).toHaveAttribute(
    'aria-current',
    'page',
  );
  // Back to the Admin tabs (they precede the main content) with Shift+Tab, then on to Workspace Settings.
  const settingsTab = areas.getByRole('link', { name: 'Workspace Settings' });
  for (
    let i = 0;
    i < 40 && !(await settingsTab.evaluate((el) => el === document.activeElement));
    i++
  )
    await page.keyboard.press('Shift+Tab');
  await expect(settingsTab).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(page).toHaveURL(/\/w\/ws-new-1\/admin\/settings$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Workspace Settings' })).toBeVisible();
  await expect(page.getByText('Not placed yet.')).toBeVisible();
  const matter = page.getByRole('textbox', { name: 'Matter number' });
  await tabTo(page, matter);
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('2026-08');
  await tabTo(page, page.getByRole('button', { name: 'Save changes' }));
  await page.keyboard.press('Enter');
  await expect(page.getByText('Workspace settings saved')).toBeVisible();
  const put = mock.workspaceWrites.find((w) => w.method === 'PUT')!;
  expect(put.ifMatch).toBe('"1"');
  expect(put.body).toMatchObject({ name: 'Gamma Matter', matterNumber: '2026-08' });

  // Deletion is a request for a second person (E20-T02; see workspace-deletions.spec.ts).
  const remove = page.getByRole('button', { name: 'Delete workspace…' });
  await expect(remove).toBeEnabled();
  await expect(remove).toHaveAccessibleDescription(/second person approves/);
});

test.describe('without an MFA session', () => {
  test.use({ api: { mfa: false } });

  test('New workspace explains the MFA requirement and starts the MFA sign-in', async ({
    page,
  }) => {
    await page.route('**/bff/login**', (route) =>
      route.fulfill({ contentType: 'text/html', body: '<title>Sign-in</title>' }),
    );
    await openPage(page, '/workspaces');
    await tabTo(page, page.getByRole('button', { name: 'New workspace' }));
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog', { name: 'New workspace' });
    await expect(dialog.getByText('Multi-factor sign-in required')).toBeVisible();
    await expect(dialog.getByRole('textbox')).toHaveCount(0);
    const signIn = page.waitForRequest('**/bff/login**');
    await tabTo(page, dialog.getByRole('button', { name: 'Sign in with MFA' }));
    await page.keyboard.press('Enter');
    expect(new URL((await signIn).url()).search).toBe(
      '?stepUp=true&returnUrl=%2Fworkspaces%3Fnew%3Dtrue',
    );
  });
});

test.describe('without the Installation Admin role', () => {
  test.use({ api: { installationPermissions: [] } });

  test('the Workspaces list offers no New workspace', async ({ page }) => {
    await openPage(page, '/workspaces');
    await expect(page.getByRole('link', { name: 'Acme v. Widget' }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'New workspace' })).toHaveCount(0);
  });
});

test('an existing workspace shows its finished setup steps', async ({ page }) => {
  await openPage(page, '/w/ws-1/admin/setup');
  await expect(page.getByText('4 of 4 steps done')).toBeVisible();
  await expect(page.getByRole('main').getByText('3 role assignments')).toBeVisible();
});
