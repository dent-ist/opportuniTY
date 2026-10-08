import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Tabs forward (or with `backwards`, Shift+Tab back) until `target` has focus, asserting a visible focus indicator on
 * every stop (WCAG 2.4.7).
 */
export async function tabTo(
  page: Page,
  target: Locator,
  maxStops = 40,
  { backwards = false }: { backwards?: boolean } = {},
): Promise<void> {
  for (let stop = 0; stop < maxStops; stop++) {
    await page.keyboard.press(backwards ? 'Shift+Tab' : 'Tab');
    const indicator = await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body) return { label: 'body', visible: true };
      // The calendar button of a native date input lives in the input's closed user-agent shadow root: the input is
      // the active element without matching :focus, and the browser draws the button's own focus ring.
      if (
        el instanceof HTMLInputElement &&
        /^(date|datetime-local)$/.test(el.type) &&
        !el.matches(':focus')
      ) {
        return { label: `${el.outerHTML.slice(0, 80)} (calendar button)`, visible: true };
      }
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
      // A tree item draws its ring on its own row (it also contains its open group): `data-focus-indicator`.
      const own = el.querySelector(':scope > [data-focus-indicator]');
      if (!visible && own) visible = ring(own);
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

/**
 * After a navigation the shell moves focus to the new page's heading (or `main`) once the page has rendered. Wait for
 * that before tabbing, or the programmatic focus can land in the middle of a Tab sequence.
 */
export async function waitForRouteFocus(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(() => {
        const main = document.getElementById('main');
        const el = document.activeElement;
        return (
          !!main &&
          (el === main || (el instanceof HTMLElement && el.matches('#main h1[data-route-focus]')))
        );
      }),
    )
    .toBe(true);
}
