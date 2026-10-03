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
