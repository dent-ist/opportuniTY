import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Searches › Privilege Conflicts (E13-T02) against the mock (e2e/support/mock-privilege-conflicts.ts): the report with
// values and reviewers, "propagate privilege call to duplicates" with the keyboard only, the CSV through the gateway
// and "Open in Documents" for a conflict group.

test('propagates a privilege call to duplicates with the keyboard only', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/searches/privilege-conflicts');
  await expect(page.getByRole('heading', { level: 1, name: 'Privilege Conflicts' })).toBeVisible();
  await expect(
    page.getByText('1 family conflict · 2 duplicate conflicts', { exact: true }),
  ).toBeVisible();
  // The default template's responsiveness field is checked too.
  expect(mock.privilegeConflicts.requests[0].query).toBe('?responsivenessField=1000');
  const family = page.getByRole('article', { name: 'Family of ACM0000001' });
  await expect(family).toContainText('Withheld member');
  await expect(family).toContainText('Responsiveness differs');
  await expect(family).toContainText('by Jamie Lee');
  await expect(page.getByRole('link', { name: 'Download CSV' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/privilege-conflicts/export?responsivenessField=1000',
  );

  // The withheld duplicate's call is preselected (the primary is Not Privileged); select the group and propagate.
  const source = page.getByRole('radio', { name: 'Use the call of ACM0000006' });
  await tabTo(page, source, 60);
  await expect(source).toBeChecked();
  const select = page.getByRole('checkbox', {
    name: /Select for propagation: Duplicates of ACM0000005/,
  });
  await tabTo(page, select, 20, { backwards: true });
  await page.keyboard.press('Space');
  await expect(page.getByText('1 group selected', { exact: true })).toBeVisible();
  const propagate = page.getByRole('button', { name: 'Propagate privilege call…' });
  await tabTo(page, propagate, 20, { backwards: true });
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('alertdialog', { name: 'Propagate privilege call' });
  await expect(dialog).toContainText('1 duplicate group (2 other listed documents');
  await tabTo(page, dialog.getByRole('button', { name: 'Start propagation' }), 5);
  await page.keyboard.press('Enter');

  await expect(
    page.locator('.toast__message').filter({ hasText: 'Propagation started.' }),
  ).toBeVisible();
  const posted = mock.privilegeConflicts.requests.find((r) => r.method === 'POST');
  expect(posted?.body).toEqual({
    groups: [{ duplicateGroupId: 'dup-1', sourceDocumentId: 'doc-6' }],
  });
  expect(posted?.idempotencyKey).toBeTruthy();
  await expect(page.getByRole('link', { name: 'View in Jobs' })).toHaveAttribute(
    'href',
    '/w/ws-1/jobs/job-priv-1',
  );

  // Running the report again shows the group resolved.
  await tabTo(page, page.getByRole('button', { name: 'Run report' }), 60, { backwards: true });
  await page.keyboard.press('Enter');
  await expect(
    page.getByText('1 family conflict · 1 duplicate conflict', { exact: true }),
  ).toBeVisible();
});

test('opens a conflict group in Documents as its search', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/searches/privilege-conflicts');
  const family = page.getByRole('article', { name: 'Family of ACM0000001' });
  await family.getByRole('link', { name: 'Open Family of ACM0000001 in Documents' }).click();
  await expect(page).toHaveURL(/\/w\/ws-1\/documents$/);
  await expect.poll(() => mock.lastSearch()?.['query']).toBe('familyid:"fam-1"');
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Showing: Family of ACM0000001.' }),
  ).toBeVisible();
});

test.describe('without Coding.WritePrivilege', () => {
  test.use({ api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'Coding.WritePrivilege') } });

  test('the report offers no propagation', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/privilege-conflicts');
    await expect(page.getByRole('article')).toHaveCount(3);
    await expect(page.getByRole('radio')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Propagate privilege call…' })).toHaveCount(0);
  });
});
