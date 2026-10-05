import type { Page } from '@playwright/test';
import type { Theme } from '../src/app/core/preferences/ui-preferences';
import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import type { MockControl } from './support/mock-api';
import { tabTo } from './support/tab';

// Search freshness and two-phase progress (E16-T07, Q-10): the freshness pill (Current / Updating / Delayed), the
// result stamp and footnote, the banner of the reviewer's own saved-but-not-searchable job, the admin detail with raw
// generations, and the polite, throttled announcements. The index is polled every 5 s while not current.

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];
const POLL_MS = 5000;

const pill = (page: Page) => page.locator('opp-freshness-status');
const banner = (page: Page) => page.getByRole('group', { name: 'Mass Edit search progress' });

/** The running Mass Edit of the mock (Alex's own) has saved everything; the index is 63% through it. */
function massEditSaved(mock: MockControl): void {
  mock.freshness.updating(370, 9);
  mock.jobs.update('job-bulk-7', {
    status: 'completed',
    committed: { done: 1000, total: 1000 },
    searchable: { done: 630, total: 1000, state: 'catchingUp', jobGeneration: 18517 },
    chunks: { pending: 0, running: 0, done: 10, failed: 0, deadLettered: 0, cancelled: 0 },
  });
}

for (const theme of THEMES) {
  test.describe(`axe, ${theme} theme`, () => {
    test.beforeEach(({ page }) => useTheme(page, theme));

    for (const state of ['current', 'updating', 'delayed'] as const) {
      test.describe(state, () => {
        test.use({ api: { freshness: state } });
        test(`freshness ${state}`, async ({ page }, testInfo) => {
          await openPage(page, '/w/ws-1/documents');
          await expect(pill(page)).toContainText(
            { current: 'Current', updating: 'Updating', delayed: 'Delayed' }[state],
          );
          if (state !== 'current') {
            await expect(
              page.getByText(/^Counts may not include [\d,]+ recent changes\.$/),
            ).toBeVisible();
          }
          await expectNoSeriousAxeViolations(page, testInfo);
          // The admin detail (every permission in the mock, Job.ViewAll included).
          await pill(page).getByRole('button').click();
          await expect(page.getByRole('group', { name: 'Search index details' })).toBeVisible();
          await expectNoSeriousAxeViolations(page, testInfo);
        });
      });
    }

    test('job banner', async ({ page, mock }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      massEditSaved(mock);
      await expect(banner(page)).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });
  });
}

test('the banner shows while a bulk job is saved but not searchable and clears after catch-up', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await expect(pill(page)).toHaveText(/Current/);
  await expect(banner(page)).toHaveCount(0);

  massEditSaved(mock);
  await expect(banner(page)).toContainText('Mass Edit saved · Search index updating… (63%)');
  await expect(banner(page).getByRole('link', { name: 'View job' })).toHaveAttribute(
    'href',
    '/w/ws-1/jobs/job-bulk-7',
  );
  // While the job catches up the index is followed, so the pill says how far behind it is.
  await expect(pill(page)).toContainText('Updating · ≈ 370 changes pending · ~9 s behind', {
    timeout: 2 * POLL_MS,
  });

  // Admin detail: raw generations (Q-10: admins and support only), opened and closed with the keyboard.
  const toggle = pill(page).getByRole('button');
  await tabTo(page, toggle, 60);
  await page.keyboard.press('Enter');
  const detail = page.getByRole('group', { name: 'Search index details' });
  await expect(detail).toContainText(
    'Index current through generation 18,432 / Job generation 18,517 (Mass Edit)',
  );
  await page.keyboard.press('Escape');
  await expect(detail).toBeHidden();
  await expect(toggle).toBeFocused();

  // The index reaches the job's generation: the job turns searchable and the banner clears.
  mock.jobs.update('job-bulk-7', {
    searchable: { done: 1000, total: 1000, state: 'current', jobGeneration: 18517 },
  });
  mock.freshness.catchUp();
  await expect(banner(page)).toHaveCount(0);
  await expect(pill(page)).toHaveText(/Current/, { timeout: 2 * POLL_MS });
});

test.describe('announcements', () => {
  test.use({ api: { freshness: 'updating' } });

  test('freshness changes are announced politely, at most once per 30 s', async ({
    page,
    mock,
  }) => {
    test.setTimeout(90_000);
    // Every message the CDK live region receives, with the time it arrived.
    await page.addInitScript(() => {
      const log: { at: number; text: string }[] = [];
      (window as unknown as { announcements: typeof log }).announcements = log;
      new MutationObserver(() => {
        const region = document.querySelector('.cdk-live-announcer-element');
        const text = region?.textContent?.trim();
        if (text && log.at(-1)?.text !== text) log.push({ at: Date.now(), text });
      }).observe(document, { subtree: true, childList: true, characterData: true });
    });
    const announced = () =>
      page.evaluate(() =>
        (
          window as unknown as { announcements: { at: number; text: string }[] }
        ).announcements.filter((a) => a.text.startsWith('Search index is')),
      );
    await openPage(page, '/w/ws-1/documents');
    await expect(page.locator('.cdk-live-announcer-element')).toHaveAttribute(
      'aria-live',
      'polite',
    );
    await expect(pill(page)).toContainText('Updating');

    // updating → delayed: announced at once (nothing was announced before).
    mock.freshness.delayed();
    await expect(pill(page)).toContainText('Delayed', { timeout: 2 * POLL_MS });
    await expect.poll(announced).toHaveLength(1);

    // delayed → updating → current within the next 30 s: shown at once, announced once, after the window.
    mock.freshness.updating();
    await expect(pill(page)).toContainText('Updating', { timeout: 2 * POLL_MS });
    mock.freshness.catchUp();
    await expect(pill(page)).toHaveText(/Current/, { timeout: 2 * POLL_MS });
    expect(await announced()).toHaveLength(1);
    await expect.poll(announced, { timeout: 35_000 }).toHaveLength(2);
    const [first, second] = await announced();
    expect(first.text).toContain('delayed');
    expect(second.text).toBe('Search index is current. New searches include every saved change.');
    expect(second.at - first.at).toBeGreaterThanOrEqual(29_500);

    // The results were served while updating; now that the index caught up they can include the changes.
    await expect(page.getByText('Counts may not include 85 recent changes.')).toBeVisible();
    await page.getByRole('button', { name: 'Include recent changes' }).click();
    await expect(page.getByText(/^Counts may not include/)).toHaveCount(0);
    await expect(page.getByText(/Results current as of/)).toBeVisible();
  });
});
