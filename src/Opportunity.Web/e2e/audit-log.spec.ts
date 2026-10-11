import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test } from './support/fixtures';
import { tabTo } from './support/tab';

// Admin › Audit and the viewer's History tab (E14-T04) against the mock (e2e/support/mock-audit.ts): the trail one
// cursor page at a time, filters that combine, a row's details narrowing the list to its job, an export run as a job
// and downloaded once completed, all with the keyboard; and a document's coding history in Review mode.

test('reads the audit trail page by page, narrows it and exports it with the keyboard only', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/admin/audit');
  await expect(page.getByRole('heading', { level: 1, name: 'Audit' })).toBeVisible();
  const events = page.getByRole('region', { name: 'Audit events' });
  await expect(events.getByRole('row')).toHaveCount(3);
  expect(mock.auditLog.requests.find((r) => r.path.endsWith('/audit-events'))?.query).toBe(
    '?limit=100',
  );

  // The next page comes by cursor, appended to the list.
  const more = page.getByRole('button', { name: 'Load more events' });
  await tabTo(page, more, 60);
  await page.keyboard.press('Enter');
  await expect(events.getByRole('row')).toHaveCount(5);
  await expect(page.getByText('Showing 4 events; more are available.')).toBeVisible();

  // A category filter (in the URL), combined with an outcome.
  const category = page.getByRole('combobox', { name: 'Category' });
  await tabTo(page, category, 60, { backwards: true });
  await category.selectOption('Coding');
  await expect(page).toHaveURL('/w/ws-1/admin/audit?category=Coding');
  await expect(events.getByRole('row')).toHaveCount(3);
  await expect(page.getByRole('combobox', { name: 'Action' })).toBeEnabled();

  // A row's details: its envelope, then "Only this job".
  const details = events.getByRole('button', { name: /^Details of Coding › Bulk chunk applied/ });
  await tabTo(page, details, 40);
  await page.keyboard.press('Enter');
  await expect(details).toHaveAttribute('aria-expanded', 'true');
  await expect(events.getByText('corr-5')).toBeVisible();
  await tabTo(page, events.getByRole('button', { name: 'Only this job' }), 10);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL('/w/ws-1/admin/audit?category=Coding&job=job-bulk-7');
  await expect(events.getByRole('row')).toHaveCount(2);
  await expectNoSeriousAxeViolations(page, test.info());

  // Export the filtered trail: a job; once completed its CSV downloads through the gateway.
  await tabTo(page, page.getByRole('button', { name: 'Export to CSV' }), 60, { backwards: true });
  await page.keyboard.press('Enter');
  await expect(
    page.locator('.toast__message').filter({ hasText: 'Audit export started.' }),
  ).toBeVisible();
  const posted = mock.auditLog.requests.find((r) => r.method === 'POST');
  expect(posted?.body).toMatchObject({ category: 'Coding', jobId: 'job-bulk-7' });
  expect(posted?.idempotencyKey).toBeTruthy();
  const exports = page.getByRole('region', { name: 'Your audit exports' });
  await expect(exports.getByText('Queued')).toBeVisible();
  const download = exports.getByRole('link', { name: /^Download CSV/ });
  await expect(download).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/audit-exports/audit-export-1/content',
  );
  await expect(exports.getByText('Completed')).toBeVisible();
});

test('shows a document’s coding history in the viewer’s History tab (E14-T04)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer.getByRole('tab', { name: 'History' })).toBeVisible();

  // Review mode binds its viewer shortcuts once the document has loaded; on a slow runner the first press can arrive
  // before that, so press until the tab is selected (pressing again on the selected tab changes nothing).
  const historyTab = viewer.getByRole('tab', { name: 'History' });
  await expect(async () => {
    await page.keyboard.press('Alt+Shift+Digit6');
    await expect(historyTab).toHaveAttribute('aria-selected', 'true', { timeout: 1000 });
  }).toPass();
  const history = viewer.getByRole('group', { name: /^Coding history of ACM/ });
  await expect(history.getByRole('row')).toHaveCount(3);
  await expect(history.getByRole('row').nth(1)).toContainText('Not Responsive');
  await expect(history.getByRole('row').nth(1)).toContainText('Jamie Lee');
  await expect(history.getByRole('link', { name: 'Job (Mass Edit or import)' })).toHaveAttribute(
    'href',
    '/w/ws-1/jobs/job-bulk-7',
  );
  await expect(history.getByRole('row').nth(2)).toContainText('Interactive');
  await expect(viewer.getByRole('link', { name: 'Audit trail of this document' })).toBeVisible();
  await expectNoSeriousAxeViolations(page, test.info());
});
