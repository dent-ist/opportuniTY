import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import type { Theme } from '../src/app/core/preferences/ui-preferences';
import { DEMO_DAT } from './support/mock-imports';

// axe in a real browser over every app-shell route, in each theme (ADR-018 §10.3): colour contrast, landmarks and
// heading structure are checked here, which jsdom cannot do. Add new routes to these lists as features land.
const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

const SIGNED_OUT_ROUTES = ['/sign-in', '/sign-in?signedOut=true', '/session-error'];

const SIGNED_IN_ROUTES = [
  '/workspaces',
  '/about',
  '/not-available',
  '/w/ws-1/documents',
  '/w/ws-1/documents?savedSearch=ss-3',
  '/w/ws-1/searches/saved',
  '/w/ws-1/searches/terms-reports',
  '/w/ws-1/searches/terms-reports/new?savedSearch=ss-1',
  '/w/ws-1/searches/terms-reports/str-1',
  '/w/ws-1/searches/terms-reports/str-2',
  '/w/ws-1/documents?termReport=str-1&term=str-1-t1',
  '/w/ws-1/jobs',
  '/w/ws-1/jobs/job-exp-3',
  '/w/ws-1/jobs/job-bulk-7',
  '/w/ws-1/imports',
  '/w/ws-1/imports/imp-1',
  '/w/ws-1/admin/fields',
  '/w/ws-1/admin/audit',
  '/w/ws-1/admin/settings',
  '/w/ws-1/admin/setup',
  '/w/ws-1/admin/highlight-sets',
];

/** Popups are rendered only while open, so each is opened and checked separately. */
const POPUPS = [
  { name: 'user menu', path: '/workspaces', trigger: { role: 'button', name: /User menu/ } },
  { name: 'job tray', path: '/w/ws-1/jobs', trigger: { role: 'button', name: /^Jobs, / } },
  {
    name: 'New workspace dialog',
    path: '/workspaces',
    trigger: { role: 'button', name: /New workspace/ },
  },
  {
    name: 'workspace switcher',
    path: '/w/ws-1/documents',
    trigger: { role: 'button', name: /Acme v\. Widget/ },
  },
] as const;

for (const theme of THEMES) {
  test.describe(`axe, ${theme} theme`, () => {
    test.beforeEach(({ page }) => useTheme(page, theme));

    test.describe('signed out', () => {
      test.use({ api: { signedIn: false } });
      for (const path of SIGNED_OUT_ROUTES) {
        test(path, async ({ page }, testInfo) => {
          await openPage(page, path);
          await expectNoSeriousAxeViolations(page, testInfo);
        });
      }
    });

    for (const path of SIGNED_IN_ROUTES) {
      test(path, async ({ page }, testInfo) => {
        await openPage(page, path);
        await expectNoSeriousAxeViolations(page, testInfo);
      });
    }

    test('workspace sidebar collapsed to icons', async ({ page }, testInfo) => {
      await openPage(page, '/w/ws-1/admin/fields');
      await page.getByRole('button', { name: 'Collapse navigation' }).click();
      await expect(page.getByRole('button', { name: 'Expand navigation' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('saved searches: actions menu, editor, share dialog, folder dialog and frozen sets', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/searches/saved');
      await page.getByRole('button', { name: 'Actions for Termination clauses' }).click();
      await expect(
        page.getByRole('menu', { name: 'Actions for Termination clauses' }),
      ).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('menuitem', { name: 'Edit…' }).click();
      const editor = page.getByRole('dialog', { name: 'Edit saved search' });
      await expect(editor.getByRole('textbox', { name: 'Keyword search' })).toHaveValue(
        '"termination" W/10 notice',
      );
      await expectNoSeriousAxeViolations(page, testInfo);
      await editor.getByRole('button', { name: 'Cancel' }).click();

      await page.getByRole('button', { name: 'Actions for Termination clauses' }).click();
      await page.getByRole('menuitem', { name: 'Share…' }).click();
      const share = page.getByRole('dialog', { name: 'Share Termination clauses' });
      await expect(share.getByRole('listitem')).toContainText('Review Team');
      await expectNoSeriousAxeViolations(page, testInfo);
      await share.getByRole('button', { name: 'Cancel' }).click();

      await page.getByRole('button', { name: 'New folder' }).click();
      await expect(page.getByRole('dialog', { name: 'New folder' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.keyboard.press('Escape');

      await page.getByRole('treeitem', { name: /Frozen sets/ }).click();
      await expect(page.getByRole('region', { name: 'Frozen sets' })).toContainText(
        'Mass Edit 2026-10-03',
      );
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('search terms reports: new report with preview and errors, running report, actions menu (#180)', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/searches/terms-reports/new');
      await page.getByRole('radio', { name: /Frozen set/ }).check();
      await page.getByRole('button', { name: 'Run report' }).click();
      await expect(page.getByRole('alert')).toContainText('Choose the frozen set to count in.');
      await page
        .getByRole('textbox', { name: /Terms, one per line/ })
        .fill('terminat*\nA\tb\nA\tc\nterminat*');
      await expect(page.getByRole('heading', { name: 'Preview: 2 terms' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await page.getByRole('radio', { name: /Whole workspace/ }).check();
      await page.getByRole('textbox', { name: /Terms, one per line/ }).fill('terminat*');
      await page.getByRole('button', { name: 'Run report' }).click();
      await expect(page.getByRole('progressbar', { name: 'Report progress' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await openPage(page, '/w/ws-1/searches/terms-reports');
      await page.getByRole('button', { name: /Actions for Key terms/ }).click();
      await expect(page.getByRole('menu', { name: /Actions for Key terms/ })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('document list views: a shared view with a pinned coding column, the Columns dialog, the Views menu and Save view', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      const viewSelect = page.getByRole('combobox', { name: 'View', exact: true });
      await viewSelect.selectOption({ label: 'First pass review' });
      await expect(
        page.locator('[role="columnheader"]', { hasText: 'Responsiveness' }),
      ).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('button', { name: 'Columns', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Columns and sort' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: 'Views', exact: true }).click();
      await expect(page.getByRole('menu', { name: 'Views' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('menuitem', { name: 'Save as new view…' }).click();
      await expect(page.getByRole('dialog', { name: 'Save view' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('keyboard shortcut dialogs open', async ({ page }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.locator('main#main').focus();
      await page.keyboard.press('Shift+Slash');
      const help = page.getByRole('dialog', { name: 'Keyboard shortcuts' });
      await expect(help).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await help.getByRole('button', { name: 'Customise shortcuts…' }).click();
      await expect(
        page.getByRole('dialog', { name: 'Customise keyboard shortcuts' }),
      ).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('document list filter row open, with active filters and the filter dialog', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.getByRole('button', { name: 'Filters' }).click();
      const fileName = page.getByRole('textbox', { name: 'Filter File Name' });
      await fileName.fill('Quarterly');
      await fileName.press('Enter');
      await expect(page.getByRole('group', { name: 'Active filters' })).toBeVisible();
      await expect(page.locator('.grid__count')).toContainText('12 documents');
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('button', { name: /^Filter Document Date/ }).click();
      await expect(page.getByRole('dialog', { name: 'Document Date filter' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('review mode, with the start-of-list notice and both panes hidden', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.getByRole('grid', { name: 'Documents' }).focus();
      await page.keyboard.press('Enter');
      const viewer = page.getByRole('region', { name: 'Viewer' });
      await expect(viewer.getByLabel('Extracted text of ACM0000001')).toBeVisible();
      await expect(page.getByRole('region', { name: 'Coding' })).toContainText('Responsiveness');
      await expectNoSeriousAxeViolations(page, testInfo);

      await page.keyboard.press('Alt+Shift+Comma');
      await expect(page.getByRole('status').getByText('Start of list.')).toBeVisible();
      await page.getByRole('button', { name: 'Coding', exact: true }).click();
      await page.getByRole('button', { name: 'Related Items', exact: true }).click();
      await expect(page.getByRole('separator', { name: 'Resize coding pane' })).toHaveAttribute(
        'aria-valuenow',
        '0',
      );
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('family groups, Related Items and the Apply to Family preview (E16-T10)', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.getByRole('button', { name: 'Group families' }).click();
      const tree = page.getByRole('treegrid', { name: 'Documents' });
      await expect(tree.getByRole('row', { name: /ACM0000004/ })).toHaveAttribute(
        'aria-level',
        '2',
      );
      await tree.getByRole('button', { name: 'Collapse family of ACM0000007' }).click();
      await expectNoSeriousAxeViolations(page, testInfo);

      await tree.getByRole('row', { name: /ACM0000003/ }).dblclick();
      const related = page.getByRole('region', { name: 'Related Items' });
      await expect(related.getByRole('row', { name: /ACM0000004/ })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await page.getByRole('button', { name: 'Apply to Family…' }).click();
      const dialog = page.getByRole('dialog', { name: 'Apply to Family' });
      await dialog.getByRole('checkbox', { name: /Responsiveness/ }).check();
      await dialog.getByRole('button', { name: 'Preview' }).click();
      await expect(dialog.getByText('Applies to')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('mass edit: selection banner, Mass Actions menu, fields, frozen confirmation and progress', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.getByRole('grid', { name: 'Documents' }).focus();
      await page.keyboard.press('Control+KeyA');
      await expect(page.getByRole('button', { name: 'Select all 250 results' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('button', { name: 'Select all 250 results' }).click();
      await page.getByRole('button', { name: 'Mass Actions' }).click();
      await expect(page.getByRole('menuitem', { name: 'Mass Edit…' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByRole('menuitem', { name: 'Mass Edit…' }).click();

      const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
      await dialog.getByRole('checkbox', { name: 'Change Responsiveness' }).check();
      await dialog.getByRole('checkbox', { name: 'Change Issues' }).check();
      await dialog.getByRole('checkbox', { name: 'Change Privilege', exact: true }).check();
      await dialog.getByRole('button', { name: 'Continue' }).click();
      await expect(dialog.getByText('Choose a value.').first()).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await dialog.getByRole('combobox', { name: 'Responsiveness value' }).selectOption('1');
      await dialog.getByRole('combobox', { name: 'Termination' }).selectOption('remove');
      await dialog.getByRole('combobox', { name: 'Privilege value' }).selectOption('21');
      await dialog.getByRole('button', { name: 'Continue' }).click();
      await expect(dialog.getByRole('textbox', { name: 'Type 250 to confirm' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await dialog.getByRole('textbox', { name: 'Type 250 to confirm' }).fill('250');
      await dialog.getByRole('button', { name: 'Apply to 250 documents' }).click();
      await expect(dialog.getByRole('heading', { name: 'Mass Edit finished' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('viewer modes: image with thumbnails, text with hits and find, metadata tooltip, native, no-text notice', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      const keyword = page.getByRole('textbox', { name: 'Keyword' });
      await keyword.fill('agreement');
      // A new result puts the cursor back on its first row: move only once it is on screen.
      const searched = page.waitForResponse(
        (r) => r.request().method() === 'POST' && /\/searches$/.test(new URL(r.url()).pathname),
      );
      await keyword.press('Enter');
      await searched;
      const grid = page.getByRole('grid', { name: 'Documents' });
      await expect(grid).not.toHaveAttribute('aria-busy', 'true');
      await grid.focus();
      for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowDown');
      await expect
        .poll(() =>
          grid.evaluate(
            (el) =>
              document.getElementById(el.getAttribute('aria-activedescendant') ?? '')?.textContent,
          ),
        )
        .toContain('ACM0000004');
      await page.keyboard.press('Enter');
      const viewer = page.getByRole('region', { name: 'Viewer' });
      // Image (doc 4), with a disabled mode's tooltip open.
      await expect(viewer.getByRole('img', { name: 'Page 1 of ACM0000004' })).toBeVisible();
      await viewer.getByRole('tab', { name: 'Production' }).hover();
      await expect(page.getByRole('tooltip')).toHaveText('Not produced');
      await expectNoSeriousAxeViolations(page, testInfo);

      // Extracted Text with a search hit and a find match highlighted.
      await viewer.getByRole('tab', { name: 'Extracted Text' }).click();
      await viewer.getByRole('searchbox', { name: 'Find in document' }).fill('pricing');
      await viewer.getByRole('button', { name: 'Next match' }).click();
      await expect(viewer.locator('#viewer-find-count')).toHaveText('1 of 1');
      // Search hits and a Highlight Set (two colours, underlined), the current hit and the per-term panel.
      await expect(viewer.getByText('Search hits (1)')).toBeVisible();
      await viewer.getByRole('button', { name: 'Next hit', exact: true }).click();
      await viewer.getByRole('button', { name: 'Terms' }).click();
      await expect(viewer.locator('#viewer-hit-panel')).toContainText('notice');
      await expectNoSeriousAxeViolations(page, testInfo);

      // Metadata, with an imported-value tooltip open.
      await viewer.getByRole('tab', { name: 'Metadata' }).click();
      await viewer.locator('.metadata__value--raw').first().focus();
      await expect(page.getByRole('tooltip')).toContainText('Imported value');
      await expectNoSeriousAxeViolations(page, testInfo);

      await viewer.getByRole('tab', { name: 'Native' }).click();
      await expect(viewer.getByRole('button', { name: 'Download native' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      // A document without text (doc 7): the fallback notice.
      await viewer.getByRole('tab', { name: 'Extracted Text' }).click();
      for (let i = 0; i < 3; i++) await page.keyboard.press('BracketRight');
      await expect(viewer.getByText('No extracted text for this document.')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('highlight sets: the editor with per-line term errors (E16-T12)', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/admin/highlight-sets');
      await page.getByRole('button', { name: 'New highlight set' }).click();
      await page.getByRole('textbox', { name: 'Name' }).fill('Names');
      await page.getByRole('textbox', { name: 'Terms' }).fill('smith\nte*');
      await page.route('**/api/v1/workspaces/ws-1/highlight-sets', (route) =>
        route.request().method() === 'POST'
          ? route.fulfill({
              status: 400,
              contentType: 'application/problem+json',
              body: JSON.stringify({
                title: 'Validation',
                status: 400,
                errors: {
                  'terms[1].expression': ['LEADING_WILDCARD: A wildcard needs 3 letters.'],
                },
              }),
            })
          : route.fallback(),
      );
      await page.getByRole('button', { name: 'Save highlight set' }).click();
      await expect(page.getByRole('alert')).toContainText('Line 2');
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('coding pane: every field type, a blocked save and a version conflict (E16-T05)', async ({
      page,
      mock,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/documents');
      await page.getByRole('grid', { name: 'Documents' }).focus();
      await page.keyboard.press('Enter');
      const coding = page.getByRole('region', { name: 'Coding' });
      await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();
      // A required field left empty blocks the save; Withhold shows Privilege Basis.
      await coding.getByRole('radio', { name: 'Withhold' }).check();
      await page.keyboard.press('Control+KeyS');
      await expect(coding.getByText('Responsiveness is required.')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      // Someone else saved first.
      mock.coding.codeAsOtherUser(1, 1000, 2, 'J. Smith');
      await coding.getByRole('radio', { name: 'Responsive', exact: true }).check();
      await page.keyboard.press('Control+KeyS');
      await expect(coding.getByRole('alert').filter({ hasText: 'Changed by' })).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      // The other field types (dates, numbers, user, multi-value text) in the Review Details layout.
      await coding.getByRole('button', { name: 'Reload current coding' }).click();
      await coding.getByRole('combobox', { name: 'Layout' }).selectOption('Review Details');
      await expect(coding.getByRole('textbox', { name: 'Pages Reviewed' })).toBeVisible();
      await coding.getByRole('textbox', { name: 'Pages Reviewed' }).fill('many');
      await page.keyboard.press('Control+KeyS');
      await expect(coding.getByText('Pages Reviewed: enter a whole number')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('import wizard: every step, custom delimiters, the profile form and validation (E08-T08)', async ({
      page,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/imports/new');
      await expectNoSeriousAxeViolations(page, testInfo);
      await page.getByLabel('Load file (DAT or CSV)').setInputFiles(DEMO_DAT);
      await page.getByRole('radio', { name: /^Overlay/ }).check();
      const next = page.getByRole('button', { name: 'Continue' });
      const step = (name: string) => page.getByRole('heading', { level: 2, name });

      await next.click();
      await expect(step('File format')).toBeFocused();
      await page.getByRole('combobox', { name: 'Delimiters' }).selectOption('custom');
      await expect(page.getByRole('textbox', { name: 'Column delimiter' })).toBeVisible();
      await expect(page.getByText('DC4 (20)')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await next.click();
      await expect(step('Field mapping')).toBeFocused();
      await page.getByRole('button', { name: 'Save as Import profile…' }).click();
      await page.getByRole('button', { name: 'Save profile' }).click();
      await expect(page.getByText('Enter a name for the import profile.')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);

      await next.click();
      await expect(step('Overlay settings')).toBeFocused();
      await expectNoSeriousAxeViolations(page, testInfo);

      await next.click();
      await expect(step('Natives, text & images')).toBeFocused();
      await expectNoSeriousAxeViolations(page, testInfo);

      await next.click();
      await expect(step('Validate & run')).toBeFocused();
      await expect(page.getByRole('checkbox', { name: /I have reviewed/ })).toBeVisible();
      await page.getByRole('button', { name: 'Start import' }).click();
      await expect(page.getByRole('alert')).toContainText('Acknowledge the warnings');
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    test('job notification and the cancel confirmation (E06-T07)', async ({
      page,
      mock,
    }, testInfo) => {
      await openPage(page, '/w/ws-1/jobs/job-bulk-7');
      mock.jobs.update('job-idx-2', { status: 'failed' });
      await expect(page.getByRole('region', { name: 'Notifications' })).toBeVisible({
        timeout: 5000,
      });
      await page.getByRole('button', { name: 'Cancel job' }).click();
      await expect(page.getByRole('alertdialog')).toBeVisible();
      await expectNoSeriousAxeViolations(page, testInfo);
    });

    for (const popup of POPUPS) {
      test(`${popup.name} open`, async ({ page }, testInfo) => {
        await openPage(page, popup.path);
        await page.getByRole(popup.trigger.role, { name: popup.trigger.name }).click();
        await expect(page.getByRole('menu').or(page.getByRole('dialog')).first()).toBeVisible();
        await expectNoSeriousAxeViolations(page, testInfo);
      });
    }
  });
}
