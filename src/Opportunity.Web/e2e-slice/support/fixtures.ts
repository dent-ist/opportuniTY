import { test as base, expect } from '@playwright/test';

export { expect };

/**
 * Every test records a HAR next to its trace and video (kept for failed tests only, `preserveOutput`) and fails on
 * uncaught errors in the page.
 */
export const test = base.extend<{ pageErrors: string[] }>({
  contextOptions: async ({ contextOptions }, use, testInfo) => {
    await use({
      ...contextOptions,
      recordHar: { path: testInfo.outputPath('network.har'), content: 'omit' },
    });
  },
  pageErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await use(errors);
      expect(errors, errors.join('\n')).toEqual([]);
    },
    { auto: true },
  ],
});
