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

// Command framework (E15-T03, familiarity guide §4). The review loop (open → Next / Save & Next → back to the
// list) is covered below (E16-T03); coding itself arrives with E16-T05.

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
  await expect(page.getByRole('region', { name: 'Viewer' })).toBeFocused();
  await expect(page.locator('.review__position')).toHaveText('Doc 1 of 250');
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

// Review mode (E16-T03, familiarity guide §3.2): the review cursor, the panes as regions and the way back.

test('reviews with the keyboard only: cursor across pages, panes as regions, Save & Next and back to the list (E16-T03)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Space');
  await expect(page.getByText('Selected: 1')).toBeVisible();
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');

  const viewer = page.getByRole('region', { name: 'Viewer' });
  const coding = page.getByRole('region', { name: 'Coding' });
  const related = page.getByRole('region', { name: 'Related Items' });
  const position = page.locator('.review__position');
  await expect(viewer).toBeFocused();
  await expect(position).toHaveText('Doc 100 of 250');
  await expect(viewer.getByLabel('Extracted text of ACM0000100')).toContainText(
    'Document ACM0000100',
  );

  // Next crosses into the second cursor page; Previous comes back; ] is the single-key alternative.
  await page.keyboard.press('Alt+Shift+Period');
  await expect(position).toHaveText('Doc 101 of 250');
  await expect(viewer.getByLabel('Extracted text of ACM0000101')).toBeVisible();
  await page.keyboard.press('BracketRight');
  await expect(position).toHaveText('Doc 102 of 250');
  await page.keyboard.press('Alt+Shift+Comma');
  await expect(position).toHaveText('Doc 101 of 250');

  // Each pane is a named region in the region cycle: viewer → coding → related.
  await page.keyboard.press('Alt+Shift+KeyG');
  await expect(coding).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyG');
  await expect(related).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyG');
  await expect(viewer).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyI');
  await expect(related).toBeFocused();

  // Save & Next (Ctrl+Enter) from anywhere in Review mode.
  await page.keyboard.press('Control+Enter');
  await expect(position).toHaveText('Doc 102 of 250');

  // The splitter collapses the Related Items pane; the cycle skips it while it is hidden.
  const splitter = page.getByRole('separator', { name: 'Resize Related Items pane' });
  await tabTo(page, splitter);
  await page.keyboard.press('Enter');
  await expect(splitter).toHaveAttribute('aria-valuenow', '0');
  await expect(page.getByRole('button', { name: 'Related Items' })).toHaveAttribute(
    'aria-expanded',
    'false',
  );
  await page.keyboard.press('Alt+Shift+KeyB');
  await expect(coding).toBeFocused();
  await page.keyboard.press('Alt+Shift+KeyB');
  await expect(viewer).toBeFocused();

  // Back to the list: focus, the reviewed document and the selection are where the reviewer left them.
  await page.keyboard.press('Escape');
  await expect(grid).toBeFocused();
  await expect
    .poll(() =>
      grid.evaluate((el) =>
        document
          .getElementById(el.getAttribute('aria-activedescendant') ?? '')
          ?.textContent?.trim(),
      ),
    )
    .toBe('ACM0000102');
  await expect(grid.getByRole('gridcell', { name: 'ACM0000102', exact: true })).toBeInViewport();
  await expect(page.getByText('Selected: 1')).toBeVisible();
});

test('back to the list restores its scroll position; pane sizes follow the user to another browser (E16-T03)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  for (let i = 0; i < 3; i++) await page.keyboard.press('PageDown');
  await expect.poll(() => grid.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
  const scrollTop = await grid.evaluate((el) => el.scrollTop);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('region', { name: 'Viewer' })).toBeFocused();

  const splitter = page.getByRole('separator', { name: 'Resize coding pane' });
  await tabTo(page, splitter);
  const saved = page.waitForRequest(
    (r) => r.method() === 'PUT' && r.url().endsWith('/api/v1/me/preferences/pane.review.coding'),
  );
  await page.keyboard.press('Shift+ArrowLeft');
  await expect(splitter).toHaveAttribute('aria-valuenow', '38');
  expect((await saved).postDataJSON()).toEqual({ size: 38, collapsed: false });

  await page.keyboard.press('Alt+Shift+KeyL');
  await expect(grid).toBeFocused();
  expect(await grid.evaluate((el) => el.scrollTop)).toBe(scrollTop);

  // Another browser: nothing stored locally; the pane size comes from the user profile on the server.
  await page.evaluate(() => localStorage.clear());
  await openPage(page, '/w/ws-1/documents');
  await grid.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('separator', { name: 'Resize coding pane' })).toHaveAttribute(
    'aria-valuenow',
    '38',
  );
});

test('prefetches the next document through the gateway as prefetch, never as a view (E16-T03)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await page.getByRole('grid', { name: 'Documents' }).focus();
  await page.keyboard.press('Enter');
  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer.getByLabel('Extracted text of ACM0000001')).toBeVisible();

  // The view beacon and the prefetch (metadata, then the first text chunk) both follow the display.
  const events = () =>
    mock.audit
      .map((e) => `${e.action} ${e.documentId} ${e.rendition ?? ''} ${e.purpose ?? e.retrievalId}`)
      .sort();
  await expect
    .poll(events)
    .toEqual([
      'Retrieved doc-1 metadata display',
      'Retrieved doc-1 text display',
      'Retrieved doc-2 metadata prefetch',
      'Retrieved doc-2 text prefetch',
      'Viewed doc-1  retrieval-2',
    ]);

  // Displaying the prefetched document records its view against the prefetch delivery; doc-3 is prefetched.
  await page.keyboard.press('BracketRight');
  await expect(viewer.getByLabel('Extracted text of ACM0000002')).toBeVisible();
  await expect
    .poll(events)
    .toEqual([
      'Retrieved doc-1 metadata display',
      'Retrieved doc-1 text display',
      'Retrieved doc-2 metadata prefetch',
      'Retrieved doc-2 text prefetch',
      'Retrieved doc-3 metadata prefetch',
      'Retrieved doc-3 text prefetch',
      'Viewed doc-1  retrieval-2',
      'Viewed doc-2  retrieval-4',
    ]);
  expect(mock.audit.filter((e) => e.action === 'Viewed' && e.documentId === 'doc-3')).toEqual([]);
});

test('keeps reviewing when the results refresh and offers Continue from next when the document left them (Q-33)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await grid.focus();
  // Row 80: far enough from the end of the first page that the list has not fetched the second.
  for (let i = 0; i < 79; i++) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  const position = page.locator('.review__position');
  await expect(position).toHaveText('Doc 80 of 250');

  // The search expires on the server, and ACM0000100 is recoded out of the results by someone else.
  mock.expireSearches();
  mock.removeDocuments(100);
  for (let n = 81; n <= 99; n++) {
    await page.keyboard.press('BracketRight');
    await expect(position).toHaveText(`Doc ${n} of 250`);
  }
  await page.keyboard.press('BracketRight');
  // At the end of the loaded page the cursor fetched the next one: the search ran again without the document.
  const status = page.getByRole('status').filter({ hasText: 'no longer in the results' });
  await expect(status).toContainText('ACM0000100 is no longer in the results.');
  await expect(status).toContainText(
    'Results refreshed: the search had expired and was run again.',
  );
  await expect(position).toHaveText('Not in the refreshed results · 249');

  await tabTo(page, page.getByRole('button', { name: 'Continue from next' }));
  await page.keyboard.press('Enter');
  await expect(position).toHaveText('Doc 100 of 249');
  await expect(page.locator('.review__control')).toHaveText('ACM0000101');
});

// Viewer modes (E16-T04, familiarity guide §3.2 and §4): mode commands, page navigation, zoom and rotate, find in
// document, the remembered mode, disabled modes with their reason, and the native download — keyboard only.
test('switches viewer modes, pages, zooms, rotates, finds and downloads with the keyboard only (E16-T04)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await tabTo(page, keyword);
  await page.keyboard.type('agreement');
  // A new result puts the cursor back on its first row: move only once it is on screen.
  const searched = page.waitForResponse(
    (r) => r.request().method() === 'POST' && /\/searches$/.test(new URL(r.url()).pathname),
  );
  await page.keyboard.press('Enter');
  await searched;
  const grid = page.getByRole('grid', { name: 'Documents' });
  await expect(grid).not.toHaveAttribute('aria-busy', 'true');
  await tabTo(page, grid);
  // Doc 4 is a PDF with page images: it opens in Image (Image → Extracted Text → Metadata).
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
  const modes = viewer.getByRole('tablist', { name: 'Viewer mode' });
  await expect(viewer).toBeFocused();
  await expect(modes.getByRole('tab', { name: 'Image' })).toHaveAttribute('aria-selected', 'true');
  const pageImage = viewer.getByRole('img', { name: 'Page 1 of ACM0000004' });
  await expect(pageImage).toBeVisible();

  // PageDown / PageUp, rotate (Alt+Shift+R), zoom (Ctrl+= / Ctrl+-) and fit (Ctrl+0) inside the viewer.
  await page.keyboard.press('PageDown');
  await expect(viewer.getByRole('img', { name: 'Page 2 of ACM0000004' })).toBeVisible();
  await expect(viewer.getByRole('button', { name: 'Page 2' })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await page.keyboard.press('PageUp');
  await expect(pageImage).toBeVisible();
  await page.keyboard.press('Alt+Shift+KeyR');
  await expect(pageImage).toHaveCSS('transform', /matrix\(0, 1, -1, 0/);
  const zoom = viewer.locator('.viewer-tools__value');
  await page.keyboard.press('Control+Minus');
  const zoomedOut = await zoom.textContent();
  await page.keyboard.press('Control+Equal');
  await expect(zoom).not.toHaveText(zoomedOut ?? '');
  await page.keyboard.press('Control+Digit0');
  await expect(viewer.getByRole('button', { name: 'Fit to width' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  // Go to page: the page box takes a number.
  await tabTo(page, viewer.getByRole('textbox', { name: 'Go to page' }));
  await page.keyboard.press('Control+A');
  await page.keyboard.type('3');
  await page.keyboard.press('Enter');
  await expect(viewer.getByRole('img', { name: 'Page 3 of ACM0000004' })).toBeVisible();
  // The thumbnails are one tab stop; arrows move between pages.
  await tabTo(page, viewer.getByRole('button', { name: 'Page 3' }));
  await page.keyboard.press('ArrowUp');
  await expect(viewer.getByRole('button', { name: 'Page 2' })).toBeFocused();
  await expect(viewer.getByRole('img', { name: 'Page 2 of ACM0000004' })).toBeVisible();

  // Alt+Shift+1: Extracted Text, with the search hit highlighted; Ctrl+F finds in the document.
  await page.keyboard.press('Alt+Shift+Digit1');
  await expect(modes.getByRole('tab', { name: 'Extracted Text' })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(viewer.getByText('1 search hit')).toBeVisible();
  await page.keyboard.press('F3');
  await expect(viewer.getByText('Hit 1 of 1')).toBeVisible();
  await viewer.getByLabel('Extracted text of ACM0000004').focus();
  await page.keyboard.press('Control+KeyF');
  const find = viewer.getByRole('searchbox', { name: 'Find in document' });
  await expect(find).toBeFocused();
  await page.keyboard.type('pricing');
  await page.keyboard.press('Enter');
  await expect(viewer.locator('#viewer-find-count')).toHaveText('1 of 1');
  await page.keyboard.press('Escape');
  await expect(find).toHaveValue('');

  // Alt+Shift+5: Metadata; the mode is remembered for the next document (a user preference).
  const saved = page.waitForRequest(
    (r) => r.method() === 'PUT' && r.url().endsWith('/api/v1/me/preferences/viewer.mode'),
  );
  await page.keyboard.press('Alt+Shift+Digit5');
  expect((await saved).postDataJSON()).toEqual({ mode: 'metadata' });
  await expect(viewer.getByRole('group', { name: 'Fields of ACM0000004' })).toBeVisible();
  await page.keyboard.press('BracketRight');
  await expect(viewer.getByRole('group', { name: 'Fields of ACM0000005' })).toBeVisible();

  // The find box went with Extracted Text, so focus moved to the viewer pane. A disabled mode stays focusable
  // and gives its reason; it cannot be selected.
  await expect(viewer).toBeFocused();
  await tabTo(page, modes.getByRole('tab', { name: 'Metadata' }));
  await page.keyboard.press('ArrowLeft');
  const production = modes.getByRole('tab', { name: 'Production' });
  await expect(production).toBeFocused();
  await expect(production).toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByRole('tooltip')).toHaveText('Not produced');
  await page.keyboard.press('Enter');
  await expect(production).toHaveAttribute('aria-selected', 'false');

  // Native: a file card and Download native through the gateway (never rendered in the browser).
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('Enter');
  await expect(modes.getByRole('tab', { name: 'Native' })).toHaveAttribute('aria-selected', 'true');
  const download = page.waitForEvent('download');
  await tabTo(page, viewer.getByRole('button', { name: 'Download native' }));
  await page.keyboard.press('Enter');
  // The browser fetches the attachment itself (outside the page's request routing), from the gateway route.
  const file = await download;
  expect(file.url()).toMatch(/\/api\/v1\/workspaces\/ws-1\/documents\/doc-5\/native$/);
  expect(file.suggestedFilename()).toBe('ACM0000005.msg');
  await expect(viewer.locator('iframe, object, embed')).toHaveCount(0);
});

test('names a document without extracted text and offers its other modes (E16-T04, #130)', async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  for (let i = 0; i < 6; i++) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer.getByText('No extracted text for this document.')).toBeVisible();
  await expect(viewer.getByText('Showing Metadata.', { exact: false })).toBeVisible();
  await expect(viewer).not.toContainText('cannot be shown');
  await tabTo(page, viewer.getByRole('button', { name: 'Show Native' }));
  await page.keyboard.press('Enter');
  await expect(viewer.getByRole('tab', { name: 'Native' })).toHaveAttribute(
    'aria-selected',
    'true',
  );
});

// Mass Actions (E16-T06): selection across pages, Mass Edit, the frozen-target confirmation and the job.

test('selects all results and mass edits them with the keyboard only; focus returns to the list (E16-T06)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await tabTo(page, grid);
  const live = page.locator('.cdk-live-announcer-element');
  await page.keyboard.press('Space');
  await expect(page.getByText('Selected: 1', { exact: true })).toBeVisible();
  await page.keyboard.press('Control+KeyA');
  await expect(page.getByText('Selected: 100', { exact: true })).toBeVisible();
  await expect(page.getByText('All 100 documents on this page are selected.')).toBeVisible();
  await expect(live).toHaveText('100 documents selected.');
  // All results: the whole search, not only the loaded page.
  await page.keyboard.press('Alt+Shift+KeyA');
  await expect(page.getByText('Selected: all 250 results')).toBeVisible();
  await expect(live).toHaveText('All 250 results selected.');

  await page.keyboard.press('Alt+Shift+KeyE');
  const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
  await expect(dialog).toBeVisible();
  await tabTo(page, dialog.getByRole('checkbox', { name: 'Change Responsiveness' }));
  await page.keyboard.press('Space');
  const value = dialog.getByRole('combobox', { name: 'Responsiveness value' });
  await tabTo(page, value);
  await page.keyboard.press('ArrowDown');
  await expect(value).toHaveValue('1');
  await tabTo(page, dialog.getByRole('checkbox', { name: 'Change Issues' }));
  await page.keyboard.press('Space');
  const pricing = dialog.getByRole('combobox', { name: 'Pricing' });
  await tabTo(page, pricing);
  await page.keyboard.press('ArrowDown');
  await expect(pricing).toHaveValue('add');
  await tabTo(page, dialog.getByRole('button', { name: 'Continue' }));
  await page.keyboard.press('Enter');

  // The frozen target: count, time and generation (admin here), the Q-07 rule; focus on the frozen set.
  const heading = dialog.getByRole('heading', { name: 'Frozen set' });
  await expect(heading).toBeFocused();
  await expect(dialog).toContainText('250 documents');
  await expect(dialog).toContainText(/Frozen at 10:42.* · generation 18,432/);
  await expect(dialog).toContainText('The list showed 250 documents.');
  await expect(dialog).toContainText(
    'Documents whose changed fields are edited by someone else after this job starts will be skipped and listed.',
  );
  expect(mock.snapshots).toHaveLength(1);
  expect(mock.snapshots[0].body).toEqual({ purpose: 'bulkCoding', query: '' });
  expect(mock.snapshots[0].idempotencyKey).toBeTruthy();

  await tabTo(page, dialog.getByRole('button', { name: 'Apply to 250 documents' }));
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('heading', { name: 'Mass Edit finished' })).toBeVisible();
  await expect(dialog.getByRole('definition').first()).toHaveText('248');
  await expect(live).toHaveText('Mass Edit finished. Updated 248 · Skipped 2 · Failed 0');
  expect(mock.bulkCoding[0].body).toEqual({
    snapshotId: 'snapshot-1',
    changes: [
      { fieldId: '1000', operation: 'set', value: 1 },
      { fieldId: '1001', operation: 'addChoices', value: [11] },
    ],
  });
  expect(mock.bulkCoding[0].idempotencyKey).toBeTruthy();

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(grid).toBeFocused();
});

test.describe('a large selection', () => {
  test.use({ api: { documents: 12_000, snapshotsMaterialize: true, selectedWhileIndexing: true } });

  test('asks for the count above 10,000 documents and warns about documents selected while indexing (E16-T06)', async ({
    page,
    mock,
  }) => {
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('grid', { name: 'Documents' }).focus();
    await page.keyboard.press('Alt+Shift+KeyA');
    await expect(page.getByText('Selected: all ≥ 10,000 (approx.) results')).toBeVisible();
    await page.keyboard.press('Alt+Shift+KeyE');
    const dialog = page.getByRole('dialog', { name: 'Mass Edit' });
    await tabTo(page, dialog.getByRole('checkbox', { name: 'Change Responsiveness' }));
    await page.keyboard.press('Space');
    await tabTo(page, dialog.getByRole('radio', { name: 'Set to' }));
    await page.keyboard.press('ArrowDown'); // Clear the value
    await expect(dialog.getByRole('radio', { name: 'Clear the value' })).toBeChecked();
    await tabTo(page, dialog.getByRole('button', { name: 'Continue' }));
    await page.keyboard.press('Enter');

    const typed = dialog.getByRole('textbox', { name: 'Type 12000 to confirm' });
    await expect(typed).toBeFocused();
    await expect(dialog).toContainText('Required for more than 10,000 documents.');
    await expect(dialog).toContainText(
      'were selected while recent changes to them were still being indexed',
    );
    const apply = dialog.getByRole('button', { name: 'Apply to 12,000 documents' });
    await expect(apply).toBeDisabled();
    await page.keyboard.type('12,000');
    await expect(apply).toBeEnabled();
    await page.keyboard.press('Enter');
    await expect(dialog.getByRole('heading', { name: 'Mass Edit finished' })).toBeVisible();
    expect(mock.bulkCoding[0].body['changes']).toEqual([{ fieldId: '1000', operation: 'clear' }]);
  });
});

// Coding pane (E16-T05, familiarity guide §3.3–§4): code → Save & Next with the keyboard only.

test('codes with the keyboard only: field jumps, access digits, required fields, Tab order and Save & Next (E16-T05)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await page.getByRole('grid', { name: 'Documents' }).focus();
  await page.keyboard.press('Enter');
  const coding = page.getByRole('region', { name: 'Coding' });
  const position = page.locator('.review__position');
  await expect(position).toHaveText('Doc 1 of 250');
  await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();

  // Alt+Shift+C, then 5: the fifth field on screen (Key Document); 1 picks Yes.
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit5');
  await expect(coding.getByRole('radio', { name: 'Yes', exact: true })).toBeFocused();
  await page.keyboard.press('Digit1');
  await expect(coding.getByRole('radio', { name: 'Yes', exact: true })).toBeChecked();
  await expect(coding.getByText('Unsaved changes')).toBeVisible();

  // Save & Next with the required Responsiveness empty: no save, no move, focus on the field with its message.
  await page.keyboard.press('Control+Enter');
  await expect(coding.getByText('Responsiveness is required.')).toBeVisible();
  await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).toBeFocused();
  await expect(position).toHaveText('Doc 1 of 250');
  expect(mock.coding.saves).toEqual([]);

  // Digits code the focused choice field; Privilege Basis appears only for Withhold / Redact.
  await page.keyboard.press('Digit1');
  await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).toBeChecked();
  await expect(coding.getByRole('group', { name: /Privilege Basis/ })).toHaveCount(0);
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit2');
  await page.keyboard.press('Digit2');
  await expect(coding.getByRole('radio', { name: 'Withhold' })).toBeChecked();
  await expect(coding.getByRole('group', { name: /Privilege Basis/ })).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(coding.getByRole('checkbox', { name: 'Attorney-Client' })).toBeFocused();
  await page.keyboard.press('Digit2');
  await expect(coding.getByRole('checkbox', { name: 'Work Product' })).toBeChecked();

  // A comment, then Tab through the actions to Save & Next.
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit7');
  const comments = coding.getByRole('textbox', { name: 'Reviewer Comments' });
  await expect(comments).toBeFocused();
  await page.keyboard.type('Pricing terms');
  await tabTo(page, coding.getByRole('button', { name: 'Save & Next' }), 6);
  await page.keyboard.press('Enter');
  await expect(position).toHaveText('Doc 2 of 250');
  expect(mock.coding.saves).toHaveLength(1);
  const save = mock.coding.saves[0];
  expect(save.documentId).toBe('doc-1');
  expect(save.ifMatch).toBe('"3"');
  expect(save.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
  expect(save.layoutId).toBe('layout-first-pass');
  expect(save.changes).toEqual(
    expect.arrayContaining([
      { fieldId: '1000', operation: 'set', value: 1 },
      { fieldId: '1002', operation: 'set', value: 22 },
      { fieldId: '1003', operation: 'set', value: [32] },
      { fieldId: '1006', operation: 'set', value: true },
      { fieldId: '1007', operation: 'set', value: 'Pricing terms' },
    ]),
  );

  // Ctrl+S saves and stays: "Saved · indexing", then searchable once the index caught up.
  await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).not.toBeChecked();
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit2');
  await page.keyboard.press('Control+KeyS');
  const status = coding.getByRole('status');
  await expect(status).toContainText('Saved · indexing');
  await expect(status).toContainText('Saved · searchable', { timeout: 10_000 });
  await expect(position).toHaveText('Doc 2 of 250');

  // Back on the first document its saved coding is shown.
  await page.keyboard.press('Alt+Shift+Comma');
  await expect(position).toHaveText('Doc 1 of 250');
  await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).toBeChecked();
  await expect(comments).toHaveValue('Pricing terms');

  // Ctrl+Enter from inside a field: the next document opens with focus back in the same field.
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit3');
  await page.keyboard.press('Control+Enter');
  await expect(position).toHaveText('Doc 2 of 250');
  await expect(coding.getByRole('radio', { name: 'Not Responsive' })).toBeFocused();
  expect(mock.coding.saves).toHaveLength(3);
});

test('a version conflict shows who changed what and when; Overwrite with mine saves on their version (E16-T05)', async ({
  page,
  mock,
}) => {
  await openPage(page, '/w/ws-1/documents');
  await page.getByRole('grid', { name: 'Documents' }).focus();
  await page.keyboard.press('Enter');
  const coding = page.getByRole('region', { name: 'Coding' });
  await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();

  // Someone else codes the document after it was read.
  mock.coding.codeAsOtherUser(1, 1000, 2, 'J. Smith');
  await page.keyboard.press('Alt+Shift+KeyC');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Digit1');
  await page.keyboard.press('Control+KeyS');
  const alert = coding.getByRole('alert').filter({ hasText: 'Changed by J. Smith' });
  await expect(alert).toContainText(/Changed by J\. Smith at \d{1,2}:\d{2}/);
  await expect(alert).toBeFocused();
  await expect(alert.getByRole('row', { name: /Responsiveness/ })).toContainText('Not Responsive');
  expect(mock.coding.saves.map((s) => s.ifMatch)).toEqual(['"3"']);

  await tabTo(page, alert.getByRole('button', { name: 'Overwrite with mine' }), 4);
  await page.keyboard.press('Enter');
  await expect(coding.getByRole('status')).toContainText('Saved');
  await expect(alert).toHaveCount(0);
  expect(mock.coding.saves.map((s) => s.ifMatch)).toEqual(['"3"', '"4"']);
  expect(mock.coding.saves[1].idempotencyKey).not.toBe(mock.coding.saves[0].idempotencyKey);
  await expect(coding.getByRole('radio', { name: 'Responsive', exact: true })).toBeChecked();
});

test.describe('without Coding.Write', () => {
  test.use({ api: { permissions: ['Document.View', 'Search.Execute'] } });

  test('the coding pane shows values without inputs and no save actions (E16-T05)', async ({
    page,
  }) => {
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('grid', { name: 'Documents' }).focus();
    for (let i = 0; i < 2; i++) await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    const coding = page.getByRole('region', { name: 'Coding' });
    const responsiveness = coding.locator('[data-coding-field="responsiveness"]');
    await expect(responsiveness).toContainText('Responsiveness');
    await expect(responsiveness.getByText('Responsive', { exact: true })).toBeVisible();
    await expect(coding.getByRole('radio')).toHaveCount(0);
    await expect(coding.getByRole('button', { name: 'Save & Next' })).toHaveCount(0);
  });
});

test.describe('while a save is indexing', () => {
  test.use({ api: { indexDelayMs: 2_500 } });

  test('the list marks the reviewer’s own saved coding until it is searchable (E16-T05)', async ({
    page,
  }) => {
    await openPage(page, '/w/ws-1/documents');
    const grid = page.getByRole('grid', { name: 'Documents' });
    await grid.focus();
    await page.keyboard.press('Enter');
    const coding = page.getByRole('region', { name: 'Coding' });
    await expect(coding.getByRole('radiogroup', { name: /Responsiveness/ })).toBeVisible();
    await page.keyboard.press('Alt+Shift+KeyC');
    await page.keyboard.press('Digit1');
    await page.keyboard.press('Digit2');
    await page.keyboard.press('Control+KeyS');
    await expect(coding.getByRole('status')).toContainText('Saved · indexing');

    await page.keyboard.press('Alt+Shift+KeyL');
    await expect(grid).toBeFocused();
    const cell = grid.getByRole('gridcell', { name: /^ACM0000001/ });
    await expect(cell).toContainText('Saved, indexing');
    await expect(cell).not.toContainText('Saved, indexing', { timeout: 15_000 });
  });
});
