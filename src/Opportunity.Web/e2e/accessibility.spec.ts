import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import type { Theme } from '../src/app/core/preferences/ui-preferences';

// axe in a real browser over every app-shell route, in each theme (ADR-018 §10.3): colour contrast, landmarks and
// heading structure are checked here, which jsdom cannot do. Add new routes to these lists as features land.
const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

const SIGNED_OUT_ROUTES = ['/sign-in', '/sign-in?signedOut=true', '/session-error'];

const SIGNED_IN_ROUTES = [
  '/workspaces',
  '/about',
  '/not-available',
  '/w/ws-1/documents',
  '/w/ws-1/jobs',
  '/w/ws-1/admin/fields',
  '/w/ws-1/admin/audit',
];

/** Popups are rendered only while open, so each is opened and checked separately. */
const POPUPS = [
  { name: 'user menu', path: '/workspaces', trigger: { role: 'button', name: /User menu/ } },
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
