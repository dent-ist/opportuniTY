import type { Page } from '@playwright/test';
import type { Theme } from '../src/app/core/preferences/ui-preferences';
import { expectNoSeriousAxeViolations } from './support/axe';
import { expect, openPage, test, useTheme } from './support/fixtures';
import { tabTo } from './support/tab';

// Redaction mode in the Image mode (E11-T04): drawing and adjusting boxes from the keyboard (WCAG 2.5.7), the
// redaction list, a concurrent edit that asks for a refresh, "Redaction requires rendered images", and axe in every
// theme. The mock API keeps redactions versioned like the server (If-Match, 412 with the current state).

const THEMES: readonly Theme[] = ['light', 'dark', 'high-contrast'];

/** Opens ACM0000004 (a PDF with page images) in Review mode, in the Image mode. */
async function openImageDocument(page: Page) {
  await openPage(page, '/w/ws-1/documents');
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
  await expect(viewer.getByRole('img', { name: 'Page 1 of ACM0000004' })).toBeVisible();
  return viewer;
}

test('a keyboard user turns on Redaction mode, adds a box and moves, resizes, relabels, redacts a full page and removes', async ({
  page,
  mock,
}) => {
  const viewer = await openImageDocument(page);
  const live = page.locator('.cdk-live-announcer-element');

  // Alt+Shift+X: Redaction mode, with the tools and the (empty) list beside the page.
  await page.keyboard.press('Alt+Shift+KeyX');
  await expect(live).toHaveText('Redaction mode on');
  const panel = viewer.getByRole('region', { name: 'Redactions' });
  await expect(panel.getByText('No redactions in this Redaction Set.')).toBeVisible();
  await expect(viewer.getByRole('button', { name: 'Redact', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );

  // New box from the keyboard: a centred box that takes focus; arrows move it, Shift+arrows resize it.
  await tabTo(page, panel.getByRole('button', { name: 'New box' }), 60);
  await page.keyboard.press('Enter');
  const box = viewer.getByRole('button', {
    name: /^Redaction 1: Attorney-Client Privilege, black box/,
  });
  await expect(box).toBeFocused();
  await expect.poll(() => mock.redactions.saves.length).toBe(1);
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Shift+ArrowDown');
  // A pause (or leaving the box) saves the keyboard change as the next version.
  await expect
    .poll(() => mock.redactions.saves.at(-1))
    .toBe('doc-4 v2: modify {"x":320000,"y":470000,"w":400000,"h":70000}');

  // The selected box takes the panel's reason: PII.
  await panel.getByRole('combobox', { name: 'Reason' }).selectOption({ label: 'PII (Privacy)' });
  await expect.poll(() => mock.redactions.saves.length).toBe(3);
  const list = panel.getByRole('list', { name: 'Redactions in this document' });
  await expect(list.getByRole('button')).toHaveCount(1);
  await expect(list.getByRole('button').first()).toContainText('p. 1');
  await expect(list.getByRole('button').first()).toContainText('PII');
  await expect(list.getByRole('button').first()).toContainText('Alex Admin');

  // Alt+Shift+P inside the redaction tools: redact the whole page after confirming.
  await panel.getByRole('button', { name: 'New box' }).focus();
  await page.keyboard.press('Alt+Shift+KeyP');
  const confirm = page.getByRole('alertdialog', { name: 'Redact full page' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: 'Redact page' }).click();
  await expect(list.getByRole('button')).toHaveCount(2);
  await expect
    .poll(() => mock.redactions.saves.at(-1))
    .toBe('doc-4 v4: add {"x":0,"y":0,"w":1000000,"h":1000000}');

  // Delete removes the focused box (Redaction.Remove); it stays in the server's history.
  // The full-page box comes first (page order, then top to bottom).
  await viewer.locator('.redaction').first().focus();
  await page.keyboard.press('Delete');
  await expect(live).toHaveText('Redaction removed.');
  await expect(list.getByRole('button')).toHaveCount(1);
  // Ctrl+Z undoes it (as a new version).
  await viewer.locator('.redaction').first().focus();
  await page.keyboard.press('Control+KeyZ');
  await expect(list.getByRole('button')).toHaveCount(2);
  expect(mock.redactions.saves).toHaveLength(6);

  // Esc ends the selection; Alt+Shift+X switches the mode off.
  await page.keyboard.press('Escape');
  await page.keyboard.press('Alt+Shift+KeyX');
  await expect(live).toHaveText('Redaction mode off');
  await expect(panel).toBeHidden();
});

test('a concurrent edit asks for a refresh and keeps the refused box on screen', async ({
  page,
  mock,
}) => {
  const viewer = await openImageDocument(page);
  await viewer.getByRole('button', { name: 'Redact', exact: true }).click();
  const panel = viewer.getByRole('region', { name: 'Redactions' });
  await expect(panel.getByText('No redactions in this Redaction Set.')).toBeVisible();

  mock.redactions.concurrentEdit(4);
  await panel.getByRole('button', { name: 'New box' }).click();
  const alert = panel.getByRole('alert');
  await expect(alert).toContainText(
    'Another user changed the redactions of this document (Avery Lee)',
  );
  await expect(alert).toContainText('Your last change was not saved');
  await expect(viewer.locator('.redaction--unsaved')).toHaveCount(1);
  await expect(panel.getByRole('button', { name: 'New box' })).toBeDisabled();

  await alert.getByRole('button', { name: 'Refresh redactions' }).click();
  await expect(panel.getByRole('alert')).toBeHidden();
  const list = panel.getByRole('list', { name: 'Redactions in this document' });
  await expect(list.getByRole('button')).toHaveCount(1);
  await expect(list).toContainText('Avery Lee');
  expect(mock.redactions.saves).toEqual([]);
});

test("Redaction mode says 'Redaction requires rendered images' for a document without them", async ({
  page,
}) => {
  await openPage(page, '/w/ws-1/documents');
  const grid = page.getByRole('grid', { name: 'Documents' });
  await expect(grid).not.toHaveAttribute('aria-busy', 'true');
  await grid.focus();
  for (let i = 0; i < 8; i++) await page.keyboard.press('ArrowDown');
  await expect
    .poll(() =>
      grid.evaluate(
        (el) =>
          document.getElementById(el.getAttribute('aria-activedescendant') ?? '')?.textContent,
      ),
    )
    .toContain('ACM0000009');
  await page.keyboard.press('Enter');
  const viewer = page.getByRole('region', { name: 'Viewer' });
  await expect(viewer.getByRole('tab', { name: 'Image' })).toHaveAttribute('aria-disabled', 'true');
  await page.keyboard.press('Alt+Shift+KeyX');
  await expect(
    viewer.getByRole('status').filter({ hasText: 'Redaction requires rendered images' }),
  ).toHaveText('Redaction requires rendered images (Image rendering in progress).');
});

for (const theme of THEMES) {
  test(`axe, ${theme} theme: Redaction mode with boxes, a selected box and the production preview`, async ({
    page,
    mock,
  }, testInfo) => {
    await useTheme(page, theme);
    mock.redactions.concurrentEdit(4);
    const viewer = await openImageDocument(page);
    await page.keyboard.press('Alt+Shift+KeyX');
    const panel = viewer.getByRole('region', { name: 'Redactions' });
    await panel.getByRole('combobox', { name: 'Box style' }).selectOption('labelled');
    await panel.getByRole('button', { name: 'New box' }).click();
    await expect(viewer.locator('.redaction')).toHaveCount(2);
    await expect(viewer.locator('.redaction--unsaved')).toHaveCount(0);
    await expectNoSeriousAxeViolations(page, testInfo);

    await panel.getByText('Production preview').click();
    await expect(viewer.locator('.redaction__label')).toHaveText('Redacted – Privileged');
    await expectNoSeriousAxeViolations(page, testInfo);
  });
}
