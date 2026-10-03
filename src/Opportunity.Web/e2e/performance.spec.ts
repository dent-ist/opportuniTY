import { appendFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, openPage, test } from './support/fixtures';

// Page-level performance budgets (ADR-018 §14) measured with the browser's own web-vitals entries (LCP, CLS, long
// tasks, Event Timing) on the production build, with the CPU slowed down to approximate the reference machine. A
// smoke check of UI latency (QA finding 14), not a benchmark: budgets are generous enough for shared CI runners.
const { page: budget } = JSON.parse(
  readFileSync(join(__dirname, '../perf-budgets.json'), 'utf8'),
) as {
  page: {
    cpuSlowdown: number;
    lcpMs: number;
    cls: number;
    totalBlockingTimeMs: number;
    interactionMs: number;
  };
};

interface Vitals {
  lcp: number;
  cls: number;
  tbt: number;
  interaction: number;
}

/** Observers are installed before the app's first script runs, so nothing is missed. */
async function observeVitals(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const v = { lcp: 0, cls: 0, tbt: 0, interaction: 0 };
    (window as unknown as { __vitals: typeof v }).__vitals = v;
    const observe = (type: string, cb: (e: PerformanceEntry) => void, extra = {}) => {
      try {
        new PerformanceObserver((list) => list.getEntries().forEach(cb)).observe({
          type,
          buffered: true,
          ...extra,
        });
      } catch {
        // Entry type not supported: the metric stays 0 and the budget check is vacuous in that browser.
      }
    };
    observe('largest-contentful-paint', (e) => (v.lcp = e.startTime));
    observe('layout-shift', (e) => {
      const shift = e as PerformanceEntry & { value: number; hadRecentInput: boolean };
      if (!shift.hadRecentInput) v.cls += shift.value;
    });
    observe('longtask', (e) => (v.tbt += Math.max(0, e.duration - 50)));
    observe(
      'event',
      (e) => {
        const timing = e as PerformanceEntry & { interactionId?: number };
        if (timing.interactionId) v.interaction = Math.max(v.interaction, e.duration);
      },
      { durationThreshold: 16 },
    );
  });
}

async function readVitals(page: Page): Promise<Vitals> {
  // Two frames so the last paint and event entries are delivered to the observers.
  await page.evaluate(
    () =>
      new Promise((r) =>
        requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 50))),
      ),
  );
  return page.evaluate(() => (window as unknown as { __vitals: Vitals }).__vitals);
}

const results: string[] = [];

test.afterAll(() => {
  const table = [
    '| Page | LCP | CLS | TBT | Interaction |',
    '|---|---|---|---|---|',
    ...results,
  ].join('\n');
  console.log(table);
  if (process.env['GITHUB_STEP_SUMMARY']) {
    appendFileSync(
      process.env['GITHUB_STEP_SUMMARY'],
      `## UI performance (CPU ×${budget.cpuSlowdown})\n\n${table}\n\n` +
        `Budgets: LCP ≤ ${budget.lcpMs} ms, CLS ≤ ${budget.cls}, TBT ≤ ${budget.totalBlockingTimeMs} ms, ` +
        `interaction ≤ ${budget.interactionMs} ms.\n\n`,
    );
  }
});

const PAGES = [
  { path: '/workspaces', interact: { role: 'button', name: /User menu/ } },
  { path: '/w/ws-1/documents', interact: { role: 'button', name: 'Admin' } },
  { path: '/w/ws-1/admin/fields', interact: { role: 'button', name: /Acme v\. Widget/ } },
] as const;

for (const { path, interact } of PAGES) {
  test(`${path} stays within the page budgets`, async ({ page }, testInfo) => {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: budget.cpuSlowdown });
    await observeVitals(page);

    await openPage(page, path);
    await page.getByRole(interact.role, { name: interact.name }).click();
    await expect(page.getByRole('menu')).toBeVisible();
    const v = await readVitals(page);

    results.push(
      `| ${path} | ${v.lcp.toFixed(0)} ms | ${v.cls.toFixed(3)} | ${v.tbt.toFixed(0)} ms | ${v.interaction.toFixed(0)} ms |`,
    );
    await testInfo.attach('vitals', {
      body: JSON.stringify(v, null, 2),
      contentType: 'application/json',
    });
    expect.soft(v.lcp, 'Largest Contentful Paint (ms)').toBeLessThanOrEqual(budget.lcpMs);
    expect.soft(v.cls, 'Cumulative Layout Shift').toBeLessThanOrEqual(budget.cls);
    expect.soft(v.tbt, 'Total Blocking Time (ms)').toBeLessThanOrEqual(budget.totalBlockingTimeMs);
    expect
      .soft(v.interaction, 'Interaction to next paint (ms)')
      .toBeLessThanOrEqual(budget.interactionMs);
  });
}

// Grid scroll frame budget (ADR-018 §14: ≥ 50 fps with 10k loaded rows). Enable when the review grid exists.
test.fixme('the review grid scrolls 10k loaded rows at ≥ 50 fps (E16-T02)', async () => {});
