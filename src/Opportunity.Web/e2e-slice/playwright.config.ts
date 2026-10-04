import { defineConfig, devices } from '@playwright/test';
import { baseURL } from './support/slice';

// Vertical-slice E2E suite (E03-T03, baseline §32) against the REAL developer Compose profile
// (deploy/docker-compose: `./opportunity.sh up && ./opportunity.sh seed`), not the mocked API of e2e/: sign in through
// Keycloak, import a 1K synthetic corpus through the API, then search → view → code → search the coding → Mass Edit →
// verify → export, once with the mouse and once with the keyboard only. Run: `npm run e2e:slice`.
// E2E_SLICE_BASE_URL (default http://localhost:8080) points at the web container; OPP_E2E_CHROMIUM at another
// Chromium binary (sandboxes without browser downloads).
const executablePath = process.env['OPP_E2E_CHROMIUM'] || undefined;
const browser = {
  ...devices['Desktop Chrome'],
  viewport: { width: 1280, height: 800 },
  launchOptions: { executablePath },
};

export default defineConfig({
  testDir: '.',
  outputDir: '../test-results/e2e-slice',
  // One stack, one shared workspace: the path runs in order, one test at a time.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  // Flake policy (docs/testing/test-strategy.md §8): no retries.
  retries: 0,
  timeout: 5 * 60_000,
  globalTimeout: 20 * 60_000,
  expect: { timeout: 15_000 },
  // Traces, videos and HARs are kept for failed tests only.
  preserveOutput: 'failures-only',
  reporter: process.env['CI']
    ? [['list'], ['junit', { outputFile: '../TestResults/e2e-slice-junit.xml' }]]
    : [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },
  projects: [
    { name: 'setup', testMatch: /\.setup\.ts$/, teardown: 'cleanup', use: browser },
    { name: 'cleanup', testMatch: /\.teardown\.ts$/ },
    {
      name: 'slice',
      testMatch: /\.spec\.ts$/,
      dependencies: ['setup'],
      use: browser,
    },
  ],
});
