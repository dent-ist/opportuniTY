import { expect, openPage, test } from './support/fixtures';
import { tabTo, waitForRouteFocus } from './support/tab';

// Jobs (E06-T07): the Jobs page, a job's page with Retry failed chunks and Cancel, the header's job tray and
// notifications, kept live through the job event stream (or the polling fallback). Keyboard only, mouse never used.

const LIVE_BUDGET_MS = 5000;

test('list → job → retry → another job → cancel → tray, with the keyboard only (E06-T07)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/jobs');
  await expect(page.getByText('Updates live')).toBeVisible();
  const table = page.getByRole('region', { name: 'Jobs' });
  await expect(table.getByRole('row')).toHaveCount(5); // header + 4 jobs (Job.ViewAll: everyone's)

  // A finished export with failed chunks: retry them.
  await tabTo(page, table.getByRole('link', { name: 'Export ACME_EXP003' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs\/job-exp-3$/);
  await waitForRouteFocus(page);
  await expect(page.getByRole('heading', { level: 1, name: 'Export ACME_EXP003' })).toBeFocused();
  await expect(
    page.getByRole('region', { name: 'Failed chunk list' }).getByRole('row'),
  ).toHaveCount(4);
  await tabTo(page, page.getByRole('button', { name: 'Retry failed chunks' }));
  await page.keyboard.press('Enter');
  await expect(page.getByText('Retrying 3 failed chunks of “Export ACME_EXP003”.')).toBeVisible();
  expect(mock.jobs.retries).toEqual([{ jobId: 'job-exp-3', idempotencyKey: expect.any(String) }]);
  await expect(page.locator('main opp-status-pill')).toHaveText('Running');
  await expect(page.getByRole('button', { name: 'Retry failed chunks' })).toHaveCount(0);

  // Back to the list, then cancel the running Mass Edit after confirming.
  await tabTo(page, page.getByRole('link', { name: 'Jobs', exact: true }).last(), 40, {
    backwards: true,
  });
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs$/);
  await waitForRouteFocus(page);
  await tabTo(page, table.getByRole('link', { name: 'Mass Edit – Responsiveness' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs\/job-bulk-7$/);
  await waitForRouteFocus(page);
  const saved = page.getByRole('progressbar', { name: 'Saved' });
  await expect(saved).toHaveAttribute(
    'aria-valuetext',
    '40% saved, 400 of 1,000, About 3 minutes left',
  );
  await expect(page.getByRole('progressbar', { name: 'Searchable' })).toHaveAttribute(
    'aria-valuetext',
    '30% searchable, 300 of 1,000',
  );
  await tabTo(page, page.getByRole('button', { name: 'Cancel job' }));
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('alertdialog', { name: 'Cancel this job?' });
  await expect(dialog.getByRole('button', { name: 'Keep running' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Cancel job' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  expect(mock.jobs.cancels).toEqual(['job-bulk-7']);
  await expect(page.locator('main opp-status-pill')).toHaveText('Cancelled', {
    timeout: LIVE_BUDGET_MS,
  });

  // The tray: the retried export and another user's index rebuild run; open it and go to the import's job.
  const tray = page.getByRole('button', { name: 'Jobs, 2 running' });
  await tabTo(page, tray, 40, { backwards: true });
  await page.keyboard.press('Enter');
  const menu = page.getByRole('menu', { name: 'Recent jobs' });
  await expect(menu.getByRole('menuitem').first()).toBeFocused();
  const target = menu.getByRole('menuitem', { name: /^Import · VOL001\.dat 2026-10-01/ });
  for (let i = 0; i < 6 && !(await target.evaluate((el) => el === document.activeElement)); i++) {
    await page.keyboard.press('ArrowDown');
  }
  await expect(target).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs\/job-imp-1$/);
  await waitForRouteFocus(page);
  await expect(page.getByRole('link', { name: 'Open the import page' })).toHaveAttribute(
    'href',
    '/w/ws-1/imports/imp-1',
  );
});

test('a status change shows within 5 s without reload, in the page, the tray and a notification', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/jobs/job-bulk-7');
  await expect(page.getByText('Updates live')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Jobs, 2 running' })).toBeVisible();

  const started = Date.now();
  mock.jobs.update('job-bulk-7', {
    status: 'completed',
    committed: { done: 1000, total: 1000 },
    searchable: { done: 1000, total: 1000, state: 'current' },
  });
  await expect(page.locator('main opp-status-pill')).toHaveText('Completed', {
    timeout: LIVE_BUDGET_MS,
  });
  const elapsed = Date.now() - started;
  expect(elapsed).toBeLessThan(LIVE_BUDGET_MS);
  await expect(page.getByRole('progressbar', { name: 'Saved' })).toHaveAttribute(
    'aria-valuenow',
    '100',
  );
  await expect(page.getByRole('button', { name: 'Jobs, 1 running' })).toBeVisible();
  await expect(
    page
      .getByRole('region', { name: 'Notifications' })
      .getByText('Mass Edit “Mass Edit – Responsiveness” completed.'),
  ).toBeVisible();

  // A job started elsewhere appears in the list without reload.
  await page.getByRole('link', { name: 'Jobs', exact: true }).last().focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs$/);
  await waitForRouteFocus(page);
  mock.jobs.add({ jobId: 'job-imp-5', type: 'import', name: 'VOL005.dat', link: 'imports/imp-5' });
  await expect(page.getByRole('link', { name: 'VOL005.dat', exact: true })).toBeVisible({
    timeout: LIVE_BUDGET_MS,
  });
});

test('a failure notification stays until dismissed and links to the job', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  mock.jobs.update('job-bulk-7', { status: 'failed', lastError: 'Worker lost its lease.' });
  const notifications = page.getByRole('region', { name: 'Notifications' });
  const failure = notifications.getByText('Mass Edit “Mass Edit – Responsiveness” failed.');
  await expect(failure).toBeVisible({ timeout: LIVE_BUDGET_MS });
  // Success notices fade after 6 s; failures do not.
  await page.waitForTimeout(6500);
  await expect(failure).toBeVisible();
  await notifications.getByRole('button', { name: 'View job' }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs\/job-bulk-7$/);
  await waitForRouteFocus(page);
  await expect(page.getByText('Worker lost its lease.')).toBeVisible();
});

test.describe('without the event stream', () => {
  test.use({ api: { jobEvents: false } });

  test('the page polls and still shows the change (Q-35 fallback, every 5 s)', async ({
    page,
    mock,
  }) => {
    await openPage(page, '/w/ws-1/jobs/job-bulk-7');
    await expect(page.getByText('Updates every 5 seconds')).toBeVisible();
    mock.jobs.update('job-bulk-7', { status: 'completed', committed: { done: 1000, total: 1000 } });
    // One polling interval plus the round trips.
    await expect(page.locator('main opp-status-pill')).toHaveText('Completed', {
      timeout: LIVE_BUDGET_MS + 1500,
    });
    expect(mock.jobs.polls.length).toBeGreaterThan(0);
  });
});

test.describe('a reviewer without Job.ViewAll or Job.Manage', () => {
  test.use({ api: { permissions: ['Document.View', 'Search.Execute', 'Coding.Write'] } });

  test('sees only their own jobs, no creator filter and no retry', async ({ page }) => {
    await openPage(page, '/w/ws-1/jobs');
    await expect(page.getByRole('link', { name: 'Search index rebuild' })).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: 'Started by' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Jobs, 1 running' })).toBeVisible();
    await page.getByRole('link', { name: 'Export ACME_EXP003' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Export ACME_EXP003' })).toBeVisible();
    await expect(page.getByText('A workspace admin can retry failed chunks.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Retry failed chunks' })).toHaveCount(0);
  });
});
