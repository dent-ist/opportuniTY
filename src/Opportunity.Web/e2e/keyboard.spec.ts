import type { Locator, Page } from '@playwright/test';
import { expect, openPage, test } from './support/fixtures';

// Keyboard-only path through the shell (WCAG 2.1.1, 2.4.1, 2.4.3, 2.4.7). Extend it through the vertical slice
// (search → view → code → next) as E16 lands; the mouse is never used here.

/** Tabs forward until `target` has focus, asserting a visible focus indicator on every stop (WCAG 2.4.7). */
async function tabTo(page: Page, target: Locator, maxStops = 40): Promise<void> {
  for (let stop = 0; stop < maxStops; stop++) {
    await page.keyboard.press('Tab');
    const indicator = await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body) return { label: 'body', visible: true };
      const ring = (node: Element) => {
        const style = getComputedStyle(node);
        return (
          (style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0) ||
          style.boxShadow !== 'none'
        );
      };
      // A composite control may draw the ring on a close wrapper via :focus-within (e.g. the query bar's textarea
      // sits over its highlight layer); that is still a visible indicator for this stop.
      let visible = ring(el);
      for (
        let up = el.parentElement, depth = 0;
        !visible && up && depth < 3;
        up = up.parentElement, depth++
      ) {
        visible = up.matches(':focus-within') && ring(up);
      }
      return { label: el.outerHTML.slice(0, 120), visible };
    });
    expect(indicator.visible, `No visible focus indicator on ${indicator.label}`).toBe(true);
    if (await target.evaluate((el) => el === document.activeElement)) return;
  }
  throw new Error(`Did not reach ${target} within ${maxStops} Tab stops`);
}

test('the skip link is the first stop and moves focus to the main region', async ({ page }) => {
  await openPage(page, '/workspaces');
  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to main content' });
  await expect(skip).toBeFocused();
  await expect(skip).toBeInViewport();
  await page.keyboard.press('Enter');
  await expect(page.locator('main#main')).toBeFocused();
});

test('opens a workspace, switches sections and reaches Admin with the keyboard only', async ({
  page,
}) => {
  await openPage(page, '/workspaces');

  await tabTo(page, page.getByRole('link', { name: 'Acme v. Widget' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/documents$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Documents' })).toBeVisible();
  await expect(page).toHaveTitle('Documents · Acme v. Widget · opportuniTY');

  const sections = page.getByRole('navigation', { name: 'Workspace sections' });
  await tabTo(page, sections.getByRole('link', { name: 'Jobs' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/jobs$/);
  await expect(sections.getByRole('link', { name: 'Jobs' })).toHaveAttribute(
    'aria-current',
    'page',
  );

  const admin = sections.getByRole('button', { name: 'Admin' });
  await tabTo(page, admin);
  await page.keyboard.press('Enter');
  const menu = page.getByRole('menu', { name: 'Admin' });
  await expect(menu).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(admin).toBeFocused();

  await page.keyboard.press('ArrowDown');
  await expect(menu).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: 'Fields' })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(menu.getByRole('menuitem', { name: 'Choices' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/admin\/choices$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Choices' })).toBeVisible();
});

test('the user menu opens, changes the theme and closes with the keyboard', async ({ page }) => {
  await openPage(page, '/workspaces');
  const trigger = page.getByRole('button', { name: /User menu/ });
  await tabTo(page, trigger);
  await page.keyboard.press('Enter');
  const dark = page.getByRole('menuitemradio', { name: 'Dark' });
  await expect(dark).toBeVisible();
  // CDK menus support typeahead and arrow keys; walk with arrows to the Dark option.
  for (let i = 0; i < 10 && !(await dark.evaluate((el) => el === document.activeElement)); i++) {
    await page.keyboard.press('ArrowDown');
  }
  await expect(dark).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});

// Command framework (E15-T03, familiarity guide §4). The review screen's own loop (code → Save & Next → next
// document) arrives with E16-T03/T05 and extends this file then; until it exists the loop is covered against the
// registry in src/app/core/commands/command-registry.spec.ts.

test('focuses the keyword search with Alt+Shift+K and /, cycles regions and opens the cheat sheet with ?', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  // Programmatic focus outside any text field (the skip link's target), standing in for "wherever I was".
  const main = page.locator('main#main');

  await main.focus();
  await page.keyboard.press('Alt+Shift+KeyK');
  await expect(keyword).toBeFocused();
  // Inside the keyword box `/` types; elsewhere it focuses the box.
  await page.keyboard.type('a/b');
  await expect(keyword).toHaveValue('a/b');
  await main.focus();
  await page.keyboard.press('Slash');
  await expect(keyword).toBeFocused();
  await expect(keyword).toHaveValue('a/b');

  // Region cycle: search panel → document list → search panel.
  const search = page.getByRole('region', { name: 'Search' });
  const list = page.getByRole('region', { name: 'Document list' });
  await page.keyboard.press('Alt+Shift+KeyG');
  await expect(list).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyG');
  await expect(search).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyB');
  await expect(list).toBeFocused();

  await page.keyboard.press('Shift+Slash');
  const help = page.getByRole('dialog', { name: 'Keyboard shortcuts' });
  await expect(help).toBeVisible();
  await expect(help.getByText('Focus keyword search')).toBeVisible();
  await expect(help.getByRole('button', { name: 'Close', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(help).toBeHidden();
  await expect(list).toBeFocused();
});

test('rebinds a shortcut and turns single keys off by keyboard; both follow the user to another browser', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const trigger = page.getByRole('button', { name: /User menu/ });
  await tabTo(page, trigger);
  await page.keyboard.press('Enter');
  const item = page.getByRole('menuitem', { name: 'Keyboard shortcuts…' });
  for (let i = 0; i < 10 && !(await item.evaluate((el) => el === document.activeElement)); i++) {
    await page.keyboard.press('ArrowDown');
  }
  await page.keyboard.press('Enter');
  const settings = page.getByRole('dialog', { name: 'Customise keyboard shortcuts' });
  await expect(settings).toBeVisible();

  const singleKey = settings.getByRole('checkbox', { name: 'Single-key shortcuts' });
  await tabTo(page, singleKey);
  await page.keyboard.press('Space');
  await expect(singleKey).not.toBeChecked();

  const add = settings.getByRole('button', { name: 'Add key for Focus keyword search' });
  await tabTo(page, add, 150);
  await page.keyboard.press('Enter');
  await expect(
    settings.getByRole('textbox', { name: 'New key for Focus keyword search' }),
  ).toBeFocused();
  const saved = page.waitForRequest(
    (r) =>
      r.method() === 'PUT' &&
      r.url().endsWith('/api/v1/me/preferences/shortcuts') &&
      !!r.postData()?.includes('Alt+Shift+KeyJ'),
  );
  await page.keyboard.press('Alt+Shift+KeyJ');
  await expect(add).toBeFocused();
  await saved;
  await page.keyboard.press('Escape');
  await expect(settings).toBeHidden();

  // Another browser: nothing stored locally; the key map comes from the user profile on the server.
  await page.evaluate(() => localStorage.clear());
  await openPage(page, '/w/ws-1/documents');
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await page.locator('main#main').focus();
  await page.keyboard.press('Slash');
  await expect(keyword).not.toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyJ');
  await expect(keyword).toBeFocused();
});
