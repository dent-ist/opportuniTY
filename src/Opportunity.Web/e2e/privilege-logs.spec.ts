import { ALL_PERMISSIONS } from './support/mock-api';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Searches › Privilege Logs (E13-T03) against the mock (e2e/support/mock-privilege-logs.ts): generate a version from a
// finalized production with a template using the keyboard only, its SHA-256 and recorded exclusion rules, the
// downloads through the gateway, and the unchanged answer when nothing changed.

test('generates a privilege log version with the keyboard only', async ({ page, mock }) => {
  await openPage(page, '/w/ws-1/searches/privilege-logs');
  await expect(page.getByRole('heading', { level: 1, name: 'Privilege Logs' })).toBeVisible();
  const production = page.getByRole('combobox', { name: 'Production' });
  // Only finalized productions are offered.
  await expect(production.locator('option')).toHaveText(['Volume 1 · ACM0000001 – ACM0000120']);
  await expect(page.getByRole('row')).toHaveCount(2);

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
  await expect(page.getByRole('row')).toHaveCount(3);
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
  await expect(page.getByRole('row')).toHaveCount(3);
});

test.describe('without Production.Create', () => {
  test.use({ api: { permissions: ALL_PERMISSIONS.filter((p) => p !== 'Production.Create') } });

  test('versions are listed but no production is offered', async ({ page }) => {
    await openPage(page, '/w/ws-1/searches/privilege-logs');
    await expect(page.getByText('needs permission to create productions')).toBeVisible();
    await expect(page.getByRole('combobox')).toHaveCount(0);
    await expect(page.getByRole('row')).toHaveCount(2);
  });
});
