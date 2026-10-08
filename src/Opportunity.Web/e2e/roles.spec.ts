import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Admin › Users & Groups and Admin › Roles & Security (E05-T08) against the role-administration contract served by
// the mock (e2e/support/mock-role-admin.ts). The signed-in user is Alex Reviewer, one of two Workspace Admins.

test('assigns roles in the matrix and adds a user with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/users-groups');
  await expect(page.getByRole('heading', { level: 1, name: 'Users & Groups' })).toBeVisible();
  await expect(page.getByRole('note')).toContainText('Search result lists may lag briefly');
  const matrix = page.getByRole('table', { name: /Roles of users and groups/ });
  await expect(matrix.getByRole('columnheader')).toHaveText([
    'User or group',
    'Workspace Admin',
    'Reviewer',
    'QC Reviewer',
    'Privilege Reviewer',
    'Production Manager',
    'Auditor (read-only)',
    'Break-glass',
    'Actions',
  ]);
  await expect(matrix.getByRole('rowheader')).toHaveCount(4);

  // Into the matrix with Tab, along it with the arrow keys, Space ticks a role and saves it at once.
  const rileyReviewer = page.getByRole('checkbox', {
    name: 'Reviewer for Riley Reviewer',
    exact: true,
  });
  await tabTo(page, page.getByRole('checkbox', { name: 'Workspace Admin for Riley Reviewer' }), 80);
  await page.keyboard.press('ArrowRight');
  await expect(rileyReviewer).toBeFocused();
  await page.keyboard.press('ArrowRight');
  const rileyQc = page.getByRole('checkbox', { name: 'QC Reviewer for Riley Reviewer' });
  await expect(rileyQc).toBeFocused();
  await page.keyboard.press('Space');
  await expect(rileyQc).toBeChecked();
  await expect(rileyQc).toBeFocused();
  expect(mock.roleAdmin.writes.at(-1)).toEqual({
    path: '/api/v1/workspaces/ws-1/role-assignments/users/user-3',
    body: { roles: ['Reviewer', 'QcReviewer'], confirmSelfRemoval: false },
    ifMatch: '"4"',
  });
  // Up into Alex's own row: adding a role to oneself is not offered, so the arrow skips to the row above or stays.
  await expect(
    page.getByRole('checkbox', { name: /^QC Reviewer for Alex Reviewer\. Another administrator/ }),
  ).toBeDisabled();

  // Add user or group: find Morgan, give them Reviewer.
  await tabTo(page, page.getByRole('button', { name: 'Add user or group' }), 80, {
    backwards: true,
  });
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Add user or group' });
  await expect(dialog).toBeVisible();
  await tabTo(page, dialog.getByRole('searchbox', { name: 'Find a user or group' }), 5);
  await page.keyboard.type('morgan');
  const morgan = dialog.getByRole('radio', { name: /Morgan Reyes/ });
  await expect(morgan).toBeVisible();
  await tabTo(page, morgan);
  await page.keyboard.press('Space');
  await expect(morgan).toBeChecked();
  const reviewer = dialog.getByRole('checkbox', { name: 'Reviewer', exact: true });
  await tabTo(page, reviewer, 20);
  await page.keyboard.press('Space');
  await expect(dialog.getByRole('checkbox', { name: /Break-glass/ })).toBeDisabled();
  await tabTo(page, dialog.getByRole('button', { name: 'Save roles' }), 20);
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(
    page.getByRole('checkbox', { name: 'Reviewer for Morgan Reyes', exact: true }),
  ).toBeChecked();
  expect(mock.roleAdmin.writes.at(-1)).toEqual({
    path: '/api/v1/workspaces/ws-1/role-assignments/users/user-4',
    body: { roles: ['Reviewer'], confirmSelfRemoval: false },
    ifMatch: '"5"',
  });
});

test('warns before an admin gives up their own last admin role, then leaves Admin', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/users-groups');
  const own = page.getByRole('checkbox', {
    name: 'Workspace Admin for Alex Reviewer',
    exact: true,
  });
  await own.focus();
  await page.keyboard.press('Space');
  const warning = page.getByRole('alertdialog', { name: 'Remove your own Workspace Admin role?' });
  await expect(warning).toContainText('You will no longer administer this workspace');

  // Cancel keeps the role and returns focus to the box.
  await page.keyboard.press('Escape');
  await expect(warning).toBeHidden();
  await expect(own).toBeChecked();
  await expect(own).toBeFocused();
  expect(mock.roleAdmin.writes).toEqual([]);

  await page.keyboard.press('Space');
  await page.getByRole('alertdialog').getByRole('button', { name: 'Remove my admin role' }).click();
  await expect(page).toHaveURL(/\/workspaces$/);
  expect(mock.roleAdmin.writes).toEqual([
    {
      path: '/api/v1/workspaces/ws-1/role-assignments/users/user-1',
      body: { roles: [], confirmSelfRemoval: true },
      ifMatch: '"4"',
    },
  ]);
});

test('the last Workspace Admin cannot be removed', async ({ page, mock }) => {
  mock.roleAdmin.principals.splice(1, 1);
  await openPage(page, '/w/ws-1/admin/users-groups');
  await expect(
    page.getByRole('checkbox', {
      name: 'Workspace Admin for Alex Reviewer. The workspace needs at least one Workspace Admin.',
    }),
  ).toBeDisabled();
  await expect(
    page.getByRole('button', { name: 'Remove Alex Reviewer from this workspace' }),
  ).toBeDisabled();
});

test('shows role permissions and changes who may see restricted documents with the keyboard', async ({
  page,
  mock,
}, testInfo) => {
  await openPage(page, '/w/ws-1/admin/roles-security');
  await expect(page.getByRole('heading', { level: 1, name: 'Roles & Security' })).toBeVisible();
  await expect(page.getByRole('note')).toContainText('Search result lists may lag briefly');
  const permissions = page.getByRole('table', { name: 'Permissions granted by each role' });
  await expect(
    permissions.getByRole('row', { name: /Assign and revoke workspace roles/ }).getByRole('cell'),
  ).toHaveText([
    'Granted',
    'Not granted',
    'Not granted',
    'Not granted',
    'Not granted',
    'Not granted',
    'Not granted',
  ]);

  // Alex holds Workspace Admin: that column is locked (self-protection).
  await expect(
    page.getByRole('checkbox', {
      name: /^Workspace Admin may see Privileged documents\. You hold this role/,
    }),
  ).toBeDisabled();
  const aeo = page.getByRole('checkbox', {
    name: "Reviewer may see Attorneys' Eyes Only documents",
    exact: true,
  });
  await tabTo(
    page,
    page.getByRole('checkbox', { name: 'Reviewer may see Privileged documents', exact: true }),
    60,
  );
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await expect(aeo).toBeFocused();
  await page.keyboard.press('Space');
  await expect(aeo).toBeChecked();
  expect(mock.roleAdmin.writes.at(-1)).toEqual({
    path: '/api/v1/workspaces/ws-1/security/restriction-classes/AttorneysEyesOnly',
    body: {
      displayName: "Attorneys' Eyes Only",
      roles: ['WorkspaceAdmin', 'PrivilegeReviewer', 'ProductionManager', 'Reviewer'],
      rules: [],
    },
    ifMatch: '"1"',
  });
  await expectNoSeriousAxeViolations(page, testInfo);
});
