import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Searches › Privilege Logs (E13-T03, #182) against the mock (e2e/support/mock-privilege-logs.ts): generate a version
// from a finalized production with a template using the keyboard only, its SHA-256 and recorded exclusion rules, the
// downloads through the gateway, and the unchanged answer when nothing changed; then, keyboard only, edit an entry's
// description inline (through coding, with If-Match), see its history, and generate the next version with a diff.

const versionRows = (page: import('@playwright/test').Page) =>
  page.getByRole('region', { name: 'Privilege log versions' }).getByRole('row');

test('generates a privilege log version with the keyboard only', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/searches/privilege-logs');
  await expect(page.getByRole('heading', { level: 1, name: 'Privilege Logs' })).toBeVisible();
  const production = page.getByRole('combobox', { name: 'Production' });
  // Only finalized productions are offered.
  await expect(production.locator('option')).toHaveText(['Volume 1 · ACM0000001 – ACM0000120']);
  await expect(versionRows(page)).toHaveCount(2);

  const template = page.getByRole('combobox', { name: 'Template' });
  await tabTo(page, template, 40);
  await page.keyboard.press('End');
  await expect(template).toHaveValue('template:tpl-1');
  await tabTo(page, page.getByRole('button', { name: 'Generate log' }), 5);
  await page.keyboard.press('Enter');

  await expect(
    page
      .locator('.toast__message')
      .filter({ hasText: 'Version 2 generated: 14 documents listed.' }),
  ).toBeVisible();
  expect(mock.privilegeLogs.requests.find((r) => r.method === 'POST')?.body).toEqual({
    productionId: 'prod-1',
    templateId: 'tpl-1',
  });
  await expect(versionRows(page)).toHaveCount(3);
  const detail = page.getByRole('article', { name: 'Volume 1 · version 2' });
  await expect(detail).toContainText('Communications with outside counsel after the complaint');
  await expect(detail).toContainText('Document Date on or after 2026-01-15');
  await expect(detail).toContainText('4 documents excluded');
  await expect(detail).toContainText('d'.repeat(64));
  const csv = detail.getByRole('link', { name: 'Download CSV' });
  await tabTo(page, csv, 30);
  await expect(csv).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/privilege-logs/plog-2/content?format=csv',
  );
  await expect(detail.getByRole('link', { name: 'Download XLSX' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/privilege-logs/plog-2/content?format=xlsx',
  );

  // The first version's details are one keyboard press away.
  const details = page.getByRole('button', { name: 'Show details of version 1 of Volume 1' });
  await tabTo(page, details, 30, { backwards: true });
  await page.keyboard.press('Enter');
  await expect(page.getByRole('article', { name: 'Volume 1 · version 1' })).toContainText(
    'No exclusion rules',
  );

  // Generating again over the same inputs makes no new version.
  await tabTo(page, page.getByRole('button', { name: 'Generate log' }), 40, { backwards: true });
  await page.keyboard.press('Enter');
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Nothing changed since version 2' }),
  ).toBeVisible();
  await expect(versionRows(page)).toHaveCount(3);
});

test('edits a description inline and generates the next version with what changed, keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/searches/privilege-logs');
  const detail = page.getByRole('article', { name: 'Volume 1 · version 1' });
  const entries = detail.getByRole('region', { name: 'Entries' });
  await expect(entries.getByRole('row')).toHaveCount(4);
  await expect(entries.getByRole('row').nth(1)).toContainText('Attorney-Client');
  await expect(detail).toContainText('This is the first version of this log');

  const edit = detail.getByRole('button', { name: 'Edit description of PRIV0002' });
  await tabTo(page, edit, 60);
  await page.keyboard.press('Enter');
  const area = detail.getByRole('textbox', { name: 'Description of PRIV0002' });
  await expect(area).toBeFocused();
  await page.keyboard.type('Draft contract comments from in-house counsel');
  await page.keyboard.press('Control+Enter');
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Description of PRIV0002 saved.' }),
  ).toBeVisible();
  await expect(edit).toBeFocused();
  const put = mock.privilegeLogs.requests.find((r) => r.method === 'PUT');
  expect(put?.body).toEqual({
    changes: [
      { fieldId: 39, operation: 'set', value: 'Draft contract comments from in-house counsel' },
    ],
  });
  await expect(detail).toContainText('1 description was edited since this version.');

  await tabTo(page, detail.getByRole('button', { name: 'Description history of PRIV0002' }), 3);
  await page.keyboard.press('Enter');
  await expect(detail.getByRole('list', { name: 'Description history of PRIV0002' })).toContainText(
    'Draft contract comments from in-house counsel',
  );

  const regenerate = detail.getByRole('button', { name: 'Generate new version' });
  await tabTo(page, regenerate, 60, { backwards: true });
  await page.keyboard.press('Enter');
  const next = page.getByRole('article', { name: 'Volume 1 · version 2' });
  await expect(next.getByRole('heading', { name: 'Changes since version 1' })).toBeVisible();
  await expect(next).toContainText('0 added · 0 removed · 1 changed · 2 unchanged');
  await expect(next.getByRole('region', { name: 'Changes since version 1' })).toContainText(
    'Description: (empty) → changed to Draft contract comments from in-house counsel',
  );
});

test.describe('without Production.Create', () => {
  test.use({ api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'Production.Create') } });

  test('versions are listed but no production is offered', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/privilege-logs');
    await expect(page.getByText('needs permission to create productions')).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Production' })).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: 'Source' })).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: 'Saved search' })).toBeVisible();
    await expect(versionRows(page)).toHaveCount(2);
  });
});

test.describe('without PrivilegeLog.Generate', () => {
  test.use({
    api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'PrivilegeLog.Generate') },
  });

  test('the Privilege Logs section is not offered', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/saved');
    await expect(page.getByRole('link', { name: 'Privilege Logs' })).toHaveCount(0);
    await openPage(page, '/w/ws-1/searches/privilege-logs');
    await expect(page.getByRole('heading', { level: 1, name: 'Privilege Logs' })).toHaveCount(0);
  });
});
