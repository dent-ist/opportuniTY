import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import { ensureCorpus } from './support/corpus';
import { expect, test } from './support/fixtures';
import {
  Api,
  saveRun,
  scope,
  signIn,
  storageState,
  users,
  type RunInfo,
  type User,
} from './support/slice';

// Baseline §32 step 1: sign in as the demo users through Keycloak, import a 1K synthetic corpus through the import
// API and wait until it is searchable. The path specs reuse the sessions and the run's scope.

for (const user of Object.keys(users) as User[]) {
  test(`signs in as ${users[user].username} through Keycloak`, async ({ page }) => {
    await signIn(page, user);
    await page.context().storageState({ path: storageState(user) });
  });
}

interface ImportResource {
  importId: string;
  report: {
    rowsRead: number | null;
    rowsImported: number;
    rowsErrored: number;
    rowsSkipped: number;
  };
  job: { status: string; statusReason: string | null; indexed: { state: string } };
}

test.describe('as the workspace admin', () => {
  test.use({ storageState: storageState('admin') });

  test('imports a 1K synthetic corpus with extracted text and natives through the API; it becomes searchable', async ({
    page,
  }) => {
    test.setTimeout(10 * 60_000);
    const corpus = ensureCorpus();
    const expected = readFileSync(corpus.dat, 'utf8').split('\r\n').filter(Boolean).length - 1;
    expect(expected).toBe(1000);
    // A fresh prefix per run: the demo workspace may hold earlier runs, and queries never see them.
    const prefix = `E2E${Date.now().toString(36).toUpperCase()}-`;

    const api = Api.of(page);
    // The demo coding fields come with `./opportunity.sh seed` (seed/demo-coding.sql).
    const layouts = await api.get<{ items: { sections: { fields: unknown[] }[] }[] }>(
      '/coding-layouts',
    );
    expect(
      layouts.items.flatMap((l) => l.sections.flatMap((s) => s.fields)).length,
      'The demo workspace has no coding layout fields: run ./opportunity.sh seed',
    ).toBeGreaterThan(0);

    const request = {
      name: `Vertical slice ${prefix}`,
      profile: { controlNumberPrefix: prefix, paths: { volumeRoot: corpus.volumeRoot } },
    };
    const started = await api.postMultipart<ImportResource>('/imports', {
      file: {
        name: basename(corpus.dat),
        mimeType: 'application/octet-stream',
        buffer: readFileSync(corpus.dat),
      },
      request: JSON.stringify(request),
    });

    const done = await api.waitFor<ImportResource>(
      `/imports/${started.importId}`,
      (i) => ['completed', 'completedWithErrors', 'failed', 'cancelled'].includes(i.job.status),
      { message: 'import job finished' },
    );
    expect(done.job.status, done.job.statusReason ?? '').toBe('completed');
    expect(done.report).toMatchObject({
      rowsRead: expected,
      rowsImported: expected,
      rowsErrored: 0,
    });

    // Searchable: the import's index tasks are applied and the whole corpus is found.
    await api.waitFor<ImportResource>(
      `/imports/${started.importId}`,
      (i) => i.job.indexed.state === 'current',
      { message: 'import indexed' },
    );
    const run: RunInfo = {
      prefix,
      importId: started.importId,
      documents: expected,
      needles: Object.fromEntries(
        Object.entries(corpus.needles).map(([term, controls]) => [
          term,
          controls.map((c) => prefix + c),
        ]),
      ),
    };
    await expect.poll(() => api.count(scope(run))).toBe(expected);
    for (const [term, controls] of Object.entries(run.needles)) {
      expect(controls.length, `documents with ${term}`).toBeGreaterThan(5);
      await expect.poll(() => api.count(`${term} AND ${scope(run)}`)).toBe(controls.length);
    }
    saveRun(run);
  });
});
