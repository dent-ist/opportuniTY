import { defineConfig, devices } from '@playwright/test';

// Browser gates (E15-T04) against the production build (`npx ng build` first) with the API mocked in the page.
// CI installs the Chromium build pinned by @playwright/test; OPP_E2E_CHROMIUM points at another binary (sandboxes
// without browser downloads).
const port = Number(process.env['E2E_PORT'] ?? 4300);
const executablePath = process.env['OPP_E2E_CHROMIUM'] || undefined;

export default defineConfig({
  testDir: '.',
  outputDir: '../test-results/e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  // Flake policy (docs/testing/test-strategy.md §8): no retries.
  retries: 0,
  reporter: process.env['CI']
    ? [['list'], ['junit', { outputFile: '../TestResults/e2e-junit.xml' }]]
    : [['list']],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    trace: 'retain-on-failure',
    viewport: { width: 1280, height: 720 },
  },
  projects: [
    {
      name: 'chromium',
      testIgnore: /performance\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 720 },
        launchOptions: { executablePath },
      },
    },
    {
      // Timed after the other projects, one test at a time, so parallel workers do not skew the numbers.
      name: 'performance',
      testMatch: /performance\.spec\.ts/,
      dependencies: ['chromium'],
      fullyParallel: false,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 720 },
        launchOptions: { executablePath },
      },
    },
  ],
  webServer: {
    command: `node e2e/support/serve.mjs ${port}`,
    cwd: '..',
    url: `http://127.0.0.1:${port}/sign-in`,
    reuseExistingServer: false,
    stdout: 'ignore',
  },
});
