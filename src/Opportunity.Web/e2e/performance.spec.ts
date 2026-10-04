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
  { path: '/w/ws-1/documents', interact: { role: 'button', name: /Acme v\. Widget/ } },
  { path: '/w/ws-1/admin/fields', interact: { role: 'button', name: /User menu/ } },
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

// Grid scroll frame budget (ADR-018 §14: ≥ 50 fps with 10k loaded rows), with the same CPU slowdown as the page budgets.
test.describe('review grid', () => {
  test.use({ api: { documents: 10_000 } });

  test('the review grid scrolls 10k loaded rows at ≥ 50 fps (E16-T02)', async ({
    page,
  }, testInfo) => {
    // 500 rows per cursor page (the reviewer's page-size preference), so 10k rows are 20 pages.
    await page.addInitScript(() => localStorage.setItem('opp.pref.grid.pageSize', '500'));
    await openPage(page, '/w/ws-1/documents');
    const grid = page.getByRole('grid', { name: 'Documents' });
    await expect(grid).toBeVisible();
    const rowPx = await grid.evaluate(
      (el) =>
        el.querySelector<HTMLElement>('[role="rowgroup"] + [role="rowgroup"] [role="row"]')!
          .offsetHeight,
    );

    // Load every page by scrolling to the end of what is loaded (cursor paging, never from offset 0).
    const loadedRows = () =>
      grid.evaluate(
        (el, px) => Math.round(el.querySelector<HTMLElement>('.grid__body')!.offsetHeight / px),
        rowPx,
      );
    for (let i = 0; i < 100 && (await loadedRows()) < 10_000; i++) {
      await grid.evaluate((el) => (el.scrollTop = el.scrollHeight));
      await page.waitForTimeout(50);
    }
    expect(await loadedRows()).toBe(10_000);
    // The last loaded row is reachable.
    await grid.evaluate((el) => (el.scrollTop = el.scrollHeight));
    await expect(grid.getByRole('gridcell', { name: 'ACM0010000', exact: true })).toBeAttached();

    // A fast fling through the loaded rows with real scroll input (a mouse-wheel gesture at 10 rows per frame),
    // timed frame by frame after a warm-up.
    const box = (await grid.boundingBox())!;
    const cdp = await page.context().newCDPSession(page);
    const fling = async (fromRow: number, rows: number) => {
      await grid.evaluate((el, top) => (el.scrollTop = top), fromRow * rowPx);
      await page.evaluate(() => {
        const w = window as unknown as { __frames: number[]; __rows: number; __stop: boolean };
        w.__frames = [];
        w.__rows = 0;
        w.__stop = false;
        const tick = (t: number) => {
          w.__frames.push(t);
          w.__rows = Math.max(
            w.__rows,
            document.querySelectorAll('[role="grid"] [role="row"]').length,
          );
          if (!w.__stop) requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      });
      await cdp.send('Input.synthesizeScrollGesture', {
        x: Math.round(box.x + box.width / 2),
        y: Math.round(box.y + box.height / 2),
        yDistance: -rows * rowPx,
        speed: 10 * rowPx * 60,
        gestureSourceType: 'mouse',
        preventFling: true,
      });
      return page.evaluate(() => {
        const w = window as unknown as { __frames: number[]; __rows: number; __stop: boolean };
        w.__stop = true;
        const times = w.__frames;
        const deltas = times.slice(1).map((v, i) => v - times[i]);
        return {
          fps: (1000 * deltas.length) / (times[times.length - 1] - times[0]),
          slowFrames: deltas.filter((d) => d > 1000 / 30).length,
          maxRowsInDom: w.__rows,
          scrolledTo: document.querySelector('[role="grid"]')!.scrollTop,
        };
      });
    };
    await fling(0, 600);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: budget.cpuSlowdown });
    const result = await fling(3_000, 3_000);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });

    results.push(
      `| review grid, 10k rows | ${result.fps.toFixed(1)} fps | ${result.slowFrames} frames > 33 ms | ${result.maxRowsInDom} rows in DOM | |`,
    );
    await testInfo.attach('grid-scroll', {
      body: JSON.stringify(result, null, 2),
      contentType: 'application/json',
    });
    expect(result.scrolledTo, 'the gesture scrolled through the rows').toBeGreaterThan(
      5_000 * rowPx,
    );
    expect(result.maxRowsInDom, 'only visible rows plus a buffer are rendered').toBeLessThan(100);
    // ADR-018 §14's ≥ 50 fps is a reference-hardware target: enforced on benchmark hardware with
    // OPPORTUNITY_STRICT_LATENCY=1 (Q-44, Q-67). Shared CI runners (software rendering, CPU slowed) record the rate
    // and fail only on clear breakage.
    const strict = process.env['OPPORTUNITY_STRICT_LATENCY'] === '1';
    expect(result.fps, 'scroll frame rate').toBeGreaterThanOrEqual(strict ? 50 : 20);
  });
});

// Review mode (E16-T03): opening a document stays within the page budgets, and with prefetch the next document is
// on screen within ADR-018 §14's 500 ms (p95) although every content response takes 300 ms.
test.describe('review mode', () => {
  test.use({ api: { contentDelayMs: 300 } });

  test('opening Review mode stays within the page budgets', async ({ page }, testInfo) => {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: budget.cpuSlowdown });
    await observeVitals(page);
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('grid', { name: 'Documents' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Extracted text of ACM0000001')).toBeVisible();
    const v = await readVitals(page);
    results.push(
      `| review mode | ${v.lcp.toFixed(0)} ms | ${v.cls.toFixed(3)} | ${v.tbt.toFixed(0)} ms | ${v.interaction.toFixed(0)} ms |`,
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

  test('the next document is visible within 500 ms p95 when prefetched (E16-T03)', async ({
    page,
    mock,
  }, testInfo) => {
    test.setTimeout(120_000);
    // Prefetches that completed, and any reported failed (such a document may be fetched for display instead).
    const prefetched = new Set<string>();
    const failed = new Set<string>();
    // A prefetch is complete with the opening mode's first content: text chunk 0, page 1's image, or (for a
    // document with neither, n % 10 === 7 in the mock) its metadata.
    const prefetchOf = (url: string) => {
      const m =
        /\/documents\/doc-(\d+)(\/text\/chunks\/0|\/pages\/1\/image)?\?purpose=prefetch/.exec(url);
      return m && (m[2] || Number(m[1]) % 10 === 7) ? `doc-${m[1]}` : undefined;
    };
    page.on('requestfinished', (r) => prefetchOf(r.url()) && prefetched.add(prefetchOf(r.url())!));
    page.on('requestfailed', (r) => {
      const id = prefetchOf(r.url());
      if (id) [prefetched, failed].forEach((s) => s.add(id));
    });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: budget.cpuSlowdown });
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('grid', { name: 'Documents' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Extracted text of ACM0000001')).toBeVisible();
    const samples: number[] = [];
    for (let n = 2; n <= 21; n++) {
      // The reviewer reads the document; meanwhile the next one has been prefetched.
      await expect.poll(() => prefetched.has(`doc-${n}`), { timeout: 15_000 }).toBe(true);
      // Timed in the page: from the key press to the frame after the next document's text is in the viewer.
      await page.evaluate((id) => {
        const w = window as unknown as { __next: Promise<number> };
        w.__next = new Promise<number>((resolve) => {
          let start = -1;
          document.addEventListener('keydown', (e) => (start = e.timeStamp), {
            capture: true,
            once: true,
          });
          const shown = () =>
            document.querySelector(
              `[data-viewer-document="${id}"][data-state="ready"] [data-viewer-content="${id}"]`,
            );
          const observer = new MutationObserver(() => {
            if (start < 0 || !shown()) return;
            observer.disconnect();
            requestAnimationFrame(() => resolve(performance.now() - start));
          });
          observer.observe(document.body, {
            subtree: true,
            childList: true,
            attributes: true,
            characterData: true,
          });
        });
      }, `doc-${n}`);
      await page.keyboard.press('BracketRight');
      samples.push(
        await page.evaluate(() => (window as unknown as { __next: Promise<number> }).__next),
      );
    }
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });

    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.ceil(0.95 * sorted.length) - 1];
    results.push(`| review mode, next document (prefetched) | p95 ${p95.toFixed(0)} ms | | | |`);
    await testInfo.attach('next-document', {
      body: JSON.stringify({ samples, p95 }, null, 2),
      contentType: 'application/json',
    });
    // Every document after the first came from its prefetch (unless that request failed): no display request.
    // Page images of an image document after its first page (page 2 is read ahead) are not part of the next-document
    // load.
    const displays = mock.audit.filter(
      (e) =>
        e.action === 'Retrieved' &&
        e.purpose === 'display' &&
        e.rendition !== 'image' &&
        e.rendition !== 'thumbnail',
    );
    expect(new Set(displays.map((e) => e.documentId))).toEqual(new Set(['doc-1']));
    expect(displays.filter((e) => e.documentId !== 'doc-1' && !failed.has(e.documentId))).toEqual(
      [],
    );
    // ADR-018 §14 target; shared runners record it and fail only on clear breakage (Q-44, as for the grid).
    const strict = process.env['OPPORTUNITY_STRICT_LATENCY'] === '1';
    expect(p95, 'next document visible, p95 (ms)').toBeLessThanOrEqual(strict ? 500 : 1_000);
  });
});

// Viewer (E16-T04): a 10 MB extracted text shows its first screen within 1 s with no long task over 200 ms. The
// text arrives in 256 KiB chunks; only the first is loaded for the first screen, and scrolling loads the next.
test.describe('viewer', () => {
  test.use({ api: { largeTextDocument: 1 } });

  test('a 10 MB text document shows its first screen in ≤ 1 s with no long tasks > 200 ms (E16-T04)', async ({
    page,
    mock,
  }, testInfo) => {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: budget.cpuSlowdown });
    await openPage(page, '/w/ws-1/documents');
    await page.getByRole('grid', { name: 'Documents' }).focus();
    await page.evaluate(() => {
      const w = window as unknown as { __firstScreen: Promise<{ ms: number; longest: number }> };
      w.__firstScreen = new Promise((resolve) => {
        let start = -1;
        let longest = 0;
        new PerformanceObserver((list) =>
          list.getEntries().forEach((e) => {
            if (start >= 0 && e.startTime + e.duration >= start)
              longest = Math.max(longest, e.duration);
          }),
        ).observe({ type: 'longtask' });
        document.addEventListener('keydown', (e) => (start = e.timeStamp), {
          capture: true,
          once: true,
        });
        const observer = new MutationObserver(() => {
          const text = document.querySelector('[data-viewer-content="doc-1"] [data-segment]');
          if (start < 0 || !text) return;
          observer.disconnect();
          requestAnimationFrame(() => {
            const ms = performance.now() - start;
            // Long tasks are reported after they end: give the observer a moment.
            setTimeout(() => resolve({ ms, longest }), 300);
          });
        });
        observer.observe(document.body, { subtree: true, childList: true, attributes: true });
      });
    });
    await page.keyboard.press('Enter');
    const result = await page.evaluate(
      () =>
        (window as unknown as { __firstScreen: Promise<{ ms: number; longest: number }> })
          .__firstScreen,
    );
    const text = page.getByLabel('Extracted text of ACM0000001');
    await expect(text).toContainText('ACM0000001 part 1 line 1:');
    await expect(page.getByText('Text truncated for search after 10 M characters')).toBeVisible();
    // Only the first of the 40 chunks was needed for the first screen.
    const chunks = () =>
      mock.audit.filter((e) => e.documentId === 'doc-1' && e.rendition === 'text');
    expect(chunks()).toHaveLength(1);
    // Scrolling to the end of what is loaded fetches the next chunk.
    await text.evaluate((el) => el.scrollTo({ top: el.scrollHeight }));
    await expect.poll(() => chunks().length).toBeGreaterThanOrEqual(2);
    await expect(text).toContainText('ACM0000001 part 2 line 1:');
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });

    results.push(
      `| viewer, 10 MB text first screen | ${result.ms.toFixed(0)} ms | | longest task ${result.longest.toFixed(0)} ms | |`,
    );
    await testInfo.attach('first-screen', {
      body: JSON.stringify(result, null, 2),
      contentType: 'application/json',
    });
    // E16-T04 acceptance targets; shared runners record them and fail only on clear breakage (Q-44).
    const strict = process.env['OPPORTUNITY_STRICT_LATENCY'] === '1';
    expect(result.ms, 'first screen (ms)').toBeLessThanOrEqual(strict ? 1_000 : 2_000);
    expect(result.longest, 'longest task (ms)').toBeLessThanOrEqual(strict ? 200 : 400);
  });
});
