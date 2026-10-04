import { expect, openPage, test } from './support/fixtures';
import { DEMO_DAT } from './support/mock-imports';
import { tabTo } from './support/tab';

// Imports (E08-T08): the keyboard-only path through the whole wizard, from the import history to the job's page and
// the Jobs section, and a pre-flight with errors blocking the start. The mouse is never used here.

test('runs the import wizard end to end with the keyboard only (E08-T08)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/imports');
  await expect(page.getByRole('link', { name: 'VOL001.dat 2026-10-01' })).toBeVisible();
  await tabTo(page, page.getByRole('link', { name: 'New Import' }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1, name: 'New Import' })).toBeVisible();
  await expect(page).toHaveTitle('New Import · Acme v. Widget · opportuniTY');

  // Step 1: the DAT through the file chooser, the volume folder and the mode.
  await tabTo(page, page.getByLabel('Load file (DAT or CSV)'));
  const chooser = page.waitForEvent('filechooser');
  await page.keyboard.press('Space');
  await (await chooser).setFiles(DEMO_DAT);
  await expect(page.getByText('VOL001.dat ·')).toBeVisible();
  await tabTo(page, page.getByRole('textbox', { name: 'Volume folder in the import share' }));
  await page.keyboard.type('matter-a/VOL001');
  await tabTo(page, page.getByRole('radio', { name: /^Append Load new/ }));
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('radio', { name: /^Append\/Overlay/ })).toBeChecked();
  await tabTo(page, page.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // Step 2: detected delimiters as glyph and code, the 20-row preview.
  await expect(page.getByRole('heading', { level: 2, name: /File format/ })).toBeFocused();
  await expect(page.getByText('DC4 (20)')).toBeVisible();
  await expect(page.getByText('þ (254)')).toBeVisible();
  await expect(
    page.getByText('Detected delimiters: Concordance-style (DC4 / þ / ®).'),
  ).toBeVisible();
  await expect(
    page.getByRole('region', { name: 'Preview of the first rows' }).locator('tbody tr'),
  ).toHaveCount(20);
  await tabTo(page, page.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // Step 3: every mapped column shows its field and type; NOTES is explicitly not imported, then "Do not import".
  await expect(page.getByRole('heading', { level: 2, name: /Field mapping/ })).toBeFocused();
  const grid = page.getByRole('region', { name: 'Field mapping' });
  const row = (column: string) =>
    grid.getByRole('row').filter({ has: page.getByRole('rowheader', { name: column }) });
  await expect(row('BEGDOC')).toContainText('Mapped, matched by alias BEGDOC');
  await expect(row('BEGDOC')).toContainText('Short Text');
  await expect(row('DATESENT')).toContainText('Date and Time');
  await expect(row('NOTES')).toContainText('Not mapped: not imported');
  await expect(page.getByText('Not imported: NOTES.')).toBeVisible();
  await tabTo(page, grid.getByRole('combobox', { name: 'Workspace field for NOTES' }));
  await page.keyboard.press('ArrowDown');
  await expect(row('NOTES')).toContainText('Do not import');
  await tabTo(page, page.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // Step 4: overlay settings.
  await expect(page.getByRole('heading', { level: 2, name: /Overlay settings/ })).toBeFocused();
  await tabTo(page, page.getByRole('radio', { name: /^Leave existing values/ }));
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('radio', { name: /^Clear existing values/ })).toBeChecked();
  await tabTo(page, page.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // Step 5: natives and text, with the rebasing example.
  await expect(
    page.getByRole('heading', { level: 2, name: /Natives, text & images/ }),
  ).toBeFocused();
  await expect(page.getByRole('combobox', { name: 'Native path column' })).toHaveValue(
    'NATIVEPATH',
  );
  await tabTo(page, page.getByRole('textbox', { name: 'Strip path prefix' }));
  await page.keyboard.type('D:\\Processing\\');
  await expect(page.getByText('matter-a/VOL001/VOL001/NATIVES/IMP0000001.msg')).toBeVisible();
  await tabTo(page, page.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // Step 6: pre-flight with warnings, which must be acknowledged before the import starts.
  await expect(page.getByRole('heading', { level: 2, name: /Validate & run/ })).toBeFocused();
  await expect(page.getByRole('cell', { name: 'NATIVE_MISSING' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download all issues (CSV)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/imports/preflight/preflight-1/issues',
  );
  await tabTo(page, page.getByRole('button', { name: 'Start import' }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('alert')).toContainText('Acknowledge the warnings');
  expect(mock.imports.starts).toHaveLength(0);
  await page.getByRole('heading', { level: 2, name: /Validate & run/ }).focus();
  await tabTo(page, page.getByRole('checkbox', { name: /I have reviewed the 2 warning/ }));
  await page.keyboard.press('Space');
  await tabTo(page, page.getByRole('button', { name: 'Start import' }));
  await page.keyboard.press('Enter');

  // The import page follows the job: Saved, then Searchable, then the report and the Jobs link.
  await expect(page).toHaveURL(/\/w\/ws-1\/imports\/imp-2$/);
  await expect(page.getByRole('progressbar', { name: 'Saved' })).toBeVisible();
  await expect(page.getByRole('progressbar', { name: 'Searchable' })).toHaveAttribute(
    'aria-valuenow',
    '2',
  );
  await expect(page.getByText('Searchable', { exact: true }).last()).toBeVisible();
  await expect(page.getByRole('link', { name: 'Error file', exact: true })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/imports/imp-2/error-file',
  );
  await expect(page.getByRole('link', { name: 'Import report (CSV)' })).toHaveAttribute(
    'href',
    '/api/v1/workspaces/ws-1/imports/imp-2/report.csv',
  );
  await expect(page.getByRole('cell', { name: 'DATE_UNPARSEABLE' }).first()).toBeVisible();

  const [start] = mock.imports.starts;
  expect(start.idempotencyKey).toBeTruthy();
  expect(start.files).toEqual(['file:VOL001.dat']);
  expect(start.request).toMatchObject({
    mode: 'appendOverlay',
    autoMap: false,
    profile: {
      mode: 'appendOverlay',
      overlay: { keyField: 'ControlNumber', blankValuesOverwrite: true, multiValue: 'replace' },
      paths: { volumeRoot: 'matter-a/VOL001', stripPrefix: 'D:\\Processing\\' },
    },
  });
  expect(start.request.profile?.columns).toContainEqual({
    column: 'NOTES',
    ignore: true,
    targets: [],
  });

  await tabTo(page, page.getByRole('link', { name: 'View in Jobs' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs\?job=job-imp-2$/);
});

test.describe('a load file with errors', () => {
  test.use({ api: { preflightBlocking: true } });

  test('validation errors block the import (E08-T08)', async ({ page, mock }) => {
    await openPage(page, '/w/ws-1/imports/new');
    const chooser = page.waitForEvent('filechooser');
    await page.getByLabel('Load file (DAT or CSV)').focus();
    await page.keyboard.press('Space');
    await (await chooser).setFiles(DEMO_DAT);
    for (const step of [
      'File format',
      'Field mapping',
      'Natives, text & images',
      'Validate & run',
    ]) {
      await page.getByRole('button', { name: 'Continue' }).focus();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('heading', { level: 2, name: step })).toBeFocused();
    }
    await expect(page.getByRole('alert').filter({ hasText: 'block this import' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'KEY_EXISTS' })).toBeVisible();
    await expect(page.getByRole('checkbox', { name: /I have reviewed/ })).toHaveCount(0);
    await page.getByRole('button', { name: 'Start import' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('alert').filter({ hasText: 'Fix the errors' })).toBeVisible();
    expect(mock.imports.starts).toHaveLength(0);
  });
});
