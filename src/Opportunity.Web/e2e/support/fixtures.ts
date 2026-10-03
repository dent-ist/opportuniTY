import { test as base, expect, type Page } from '@playwright/test';
import type { Theme } from '../../src/app/core/preferences/ui-preferences';
import { mockApi, type MockApiOptions } from './mock-api';

export { expect };

interface Fixtures {
  /** Per-test API mock options (`test.use({ api: { signedIn: false } })`). */
  api: MockApiOptions;
  /** API paths the mock had no answer for; the test fails if any were requested. */
  unhandledApi: string[];
  consoleErrors: string[];
}

/**
 * Every browser test runs against the mocked API and fails on uncaught errors, console errors (CSP violations
 * surface there: the server sends the production policy) and requests to endpoints the mock does not know.
 */
export const test = base.extend<Fixtures>({
  api: [{}, { option: true }],
  unhandledApi: [
    async ({ page, api }, use) => {
      const unhandled = await mockApi(page, api);
      await use(unhandled);
      expect(unhandled, `Unmocked API requests: ${unhandled.join(', ')}`).toEqual([]);
    },
    { auto: true },
  ],
  consoleErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
      page.on('console', (m) => {
        // Chromium logs every 4xx/5xx response; expected ones (401 when signed out) are API behaviour, and
        // unexpected endpoints are caught by `unhandledApi`.
        if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) {
          errors.push(`console: ${m.text()}`);
        }
      });
      await use(errors);
      expect(errors, errors.join('\n')).toEqual([]);
    },
    { auto: true },
  ],
});

/** Stores the theme preference before the app boots (the key PreferenceStorage uses). */
export async function useTheme(page: Page, theme: Theme): Promise<void> {
  await page.addInitScript((value) => {
    localStorage.setItem('opp.pref.ui', JSON.stringify({ theme: value }));
  }, theme);
}

/** Navigates and waits until the routed page has rendered its heading and settled. */
export async function openPage(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await expect(page.locator('main h1').first()).toBeVisible();
  await page.waitForLoadState('networkidle');
}
