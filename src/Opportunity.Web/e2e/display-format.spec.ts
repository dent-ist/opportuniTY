import type { Page } from '@playwright/test';
import { expect, openPage, test } from './support/fixtures';

// E15-T05 (Q-28): dates follow the user's date format and display time zone, and a change in the user menu applies
// at once, without reloading the page.

test.use({ timezoneId: 'America/New_York' });

async function choose(page: Page, item: string): Promise<void> {
  await page.getByRole('button', { name: /User menu/ }).click();
  await page.getByRole('menuitemradio', { name: item }).click();
}

test('switches date format and time zone from the user menu without a reload', async ({ page }) => {
  await openPage(page, '/w/ws-1/searches/terms-reports/str-1');
  const about = page.getByRole('region', { name: 'About this report' });
  // The workspace display time zone (UTC in the mock) with a zone indicator.
  await expect(about).toContainText(/Taken 10\/03\/2026, \d\d:\d\d [AP]M UTC/);
  await page.evaluate(() => ((window as unknown as { notReloaded: boolean }).notReloaded = true));

  await choose(page, 'DD/MM/YYYY');
  await expect(about).toContainText(/Taken 03\/10\/2026, \d\d:\d\d UTC/);

  await choose(page, 'YYYY-MM-DD (ISO 8601)');
  await expect(about).toContainText(/Taken 2026-10-03 \d\d:\d\d UTC/);

  await choose(page, 'This computer (America/New_York)');
  await expect(about).toContainText(/Taken 2026-10-03 06:41 (EDT|GMT-4)/);

  await choose(page, 'Workspace (UTC)');
  await expect(about).toContainText(/Taken 2026-10-03 \d\d:\d\d UTC/);

  expect(
    await page.evaluate(() => (window as unknown as { notReloaded?: boolean }).notReloaded),
  ).toBe(true);
});
