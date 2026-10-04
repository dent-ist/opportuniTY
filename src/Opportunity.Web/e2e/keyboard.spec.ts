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

  // Admin opens its first area; its own areas are tabs across the top of the main region (Q-64).
  await tabTo(page, sections.getByRole('link', { name: 'Admin' }));
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/w\/ws-1\/admin\/fields$/);
  await expect(sections.getByRole('link', { name: 'Admin' })).toHaveAttribute(
    'aria-current',
    'true',
  );
  const areas = page.getByRole('navigation', { name: 'Admin' });
  await expect(areas.getByRole('link', { name: 'Fields' })).toHaveAttribute('aria-current', 'page');
  await tabTo(page, areas.getByRole('link', { name: 'Choices' }));
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

test('suggests the workspace custom fields and choices; recent searches follow the user to a new session', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await keyword.focus();
  await page.keyboard.type('resp');
  const suggestions = page.getByRole('listbox', { name: 'Suggestions' });
  await expect(suggestions.getByRole('option', { name: /responsiveness/ })).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(keyword).toHaveValue('responsiveness:');
  await expect(suggestions.getByRole('option', { name: /Not Responsive/ })).toBeVisible();
  await page.keyboard.press('Escape');

  const recorded = page.waitForRequest(
    (r) => r.method() === 'POST' && r.url().endsWith('/api/v1/workspaces/ws-1/query-history'),
  );
  await page.keyboard.type('"Responsive"');
  await page.keyboard.press('Enter');
  await recorded;

  // Signed in again elsewhere: nothing in browser storage; the history comes from the server.
  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  expect(stored).not.toContain('responsiveness');
  await page.evaluate(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
  await openPage(page, '/w/ws-1/documents');
  await keyword.focus();
  await page.keyboard.press('Alt+ArrowDown');
  const recent = page.getByRole('listbox', { name: 'Recent searches' });
  await expect(recent.getByRole('option', { name: /responsiveness:"Responsive"/ })).toBeVisible();
});

test('works the document list with the keyboard: rows, selection and open (E16-T02)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  const active = () =>
    grid.evaluate((el) =>
      document.getElementById(el.getAttribute('aria-activedescendant') ?? '')?.textContent?.trim(),
    );
  await expect.poll(active).toBe('ACM0000001');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Space');
  await page.keyboard.press('Shift+ArrowDown');
  await expect(page.getByText('Selected: 2')).toBeVisible();
  await page.keyboard.press('End');
  await expect.poll(active).toBe('ACM0000100');
  await page.keyboard.press('Control+Home');
  await expect.poll(active).toBe('ACM0000001');
  await page.keyboard.press('Enter');
  await expect(
    page.getByText('Review mode is not available yet (ACM0000001).').first(),
  ).toBeVisible();
});

test('filters the document list from the filter row with the keyboard only (#191)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const filters = page.getByRole('button', { name: 'Filters' });
  await tabTo(page, filters);
  await page.keyboard.press('Enter');
  await expect(filters).toHaveAttribute('aria-pressed', 'true');

  // Tab reaches the row (one stop for all of its controls); the arrow keys move between filters.
  const grid = page.getByRole('grid', { name: 'Documents' });
  const controlNumber = grid.getByRole('textbox', { name: 'Filter Control Number' });
  await tabTo(page, controlNumber);
  await page.keyboard.press('ArrowRight');
  await expect(grid.getByRole('button', { name: 'Control Number filter options' })).toBeFocused();
  await page.keyboard.press('ArrowRight');
  const date = grid.getByRole('button', { name: /^Filter Document Date/ });
  await expect(date).toBeFocused();
  await page.keyboard.press('ArrowRight');
  const fileName = grid.getByRole('textbox', { name: 'Filter File Name' });
  await expect(fileName).toBeFocused();

  const searched = (query: string) =>
    page.waitForRequest(
      (r) =>
        r.method() === 'POST' &&
        r.url().endsWith('/api/v1/workspaces/ws-1/searches') &&
        (r.postDataJSON() as { query: string }).query === query,
    );
  let request = searched('filename:*Quarterly*');
  await page.keyboard.type('Quarterly');
  await page.keyboard.press('Enter');
  await request;
  await expect(page.locator('.grid__count')).toContainText('12 documents');
  await expect(page.getByRole('group', { name: 'Active filters' })).toContainText('File Name');
  await expect(fileName).toBeFocused();

  // A date range in the filter dialog; Enter applies and focus returns to the filter.
  await page.keyboard.press('Home');
  await page.keyboard.press('ArrowLeft');
  await expect(date).toBeFocused();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Document Date filter' });
  await expect(dialog).toBeVisible();
  const from = dialog.getByLabel('From');
  await tabTo(page, from, 10);
  await from.fill('2024-01-01');
  request = searched('filename:*Quarterly* AND date:[2024-01-01 TO *]');
  await page.keyboard.press('Enter');
  await request;
  await expect(dialog).toBeHidden();
  await expect(date).toBeFocused();
  await expect(date).toHaveAccessibleName('Filter Document Date: from 2024-01-01');

  // Escape clears the focused filter; with the last one gone the full list is back.
  request = searched('filename:*Quarterly*');
  await page.keyboard.press('Escape');
  await request;
  await page.keyboard.press('ArrowRight');
  await expect(fileName).toBeFocused();
  request = searched('');
  await page.keyboard.press('Escape');
  await request;
  await expect(fileName).toHaveValue('');
  await expect(page.locator('.grid__count')).toContainText('250 documents');
  await expect(page.getByRole('group', { name: 'Active filters' })).toBeHidden();

  // The command hides the row again and puts focus back on the list.
  await page.keyboard.press('Alt+Shift+KeyU');
  await expect(fileName).toBeHidden();
  await expect(grid).toBeFocused();
  await expect(filters).toHaveAttribute('aria-pressed', 'false');
});
