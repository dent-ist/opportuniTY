import { expect, test } from './support/fixtures';
import { runSearch } from './support/path';
import { Api, baseURL, documentsPath, loadRun, scope, storageState } from './support/slice';

// E16-T08 stale hit on the real stack (Q-12, Q-13): the reviewer's list is loaded, then an administrator walls one of
// its documents off from the reviewer. Opening that row shows the standard no-access state, the gateway answers like
// a missing document, and the browser requests no artifact of it (text, pages, images, native, view); back in the list
// the row is "No longer available". The wall is removed again whatever happens, so later runs see every document.

/** Content artifacts of a document, and the view beacon. */
const ARTIFACT = /\/(text|pages|native|views)(\/|$)/;

interface SearchPage {
  readonly items: readonly { readonly documentId: string; readonly controlNumber: string }[];
}

test.use({ storageState: storageState('reviewer') });

test('a hit walled off between search and open shows the no-access state and requests no artifact', async ({
  page,
  browser,
}) => {
  const run = loadRun();
  await page.goto(documentsPath);
  await expect(page.getByRole('heading', { level: 1, name: 'Documents' })).toBeVisible();
  // Read the hits as the response arrives: the list updates the URL right after, and Chromium then no longer has the
  // body of a response from before that navigation.
  let hits: SearchPage | undefined;
  const searched = page.waitForResponse(async (r) => {
    if (r.request().method() !== 'POST' || !r.url().endsWith('/searches') || !r.ok()) return false;
    hits = (await r.json()) as SearchPage;
    return true;
  });
  await runSearch(page, scope(run));
  await searched;
  const target = hits!.items[2];
  const rows = page.getByRole('grid', { name: 'Documents' }).locator('.grid__body [role="row"]');
  await expect(rows.nth(2)).toContainText(target.controlNumber);

  const me = (await (await page.request.get(`${baseURL}/api/v1/me`)).json()) as { userId: string };
  const adminContext = await browser.newContext({ storageState: storageState('admin') });
  const admin = Api.of(await adminContext.newPage());
  let wallId: string | null = null;
  try {
    const wall = await admin.post<{ wallId: string }>('/security/walls', {
      name: `E16-T08 stale hit ${run.prefix}`,
      members: { userIds: [me.userId], groups: [] },
      scope: { documentIds: [target.documentId], custodians: [], choices: [] },
    });
    wallId = wall.wallId;

    const requests: string[] = [];
    page.on('request', (r) => {
      if (r.url().includes(target.documentId)) requests.push(`${r.method()} ${r.url()}`);
    });
    await rows.nth(2).dblclick();
    const viewer = page.getByRole('region', { name: 'Viewer' });
    await expect(viewer.getByRole('heading', { name: 'Document not available' })).toBeVisible();
    await expect(page.locator('.review__bar')).toContainText('No longer available');
    await expect(page.locator('.review__bar')).not.toContainText(target.controlNumber);
    await expect(page.getByRole('region', { name: 'Coding' })).toContainText('No coding to show');
    await page.waitForLoadState('networkidle');
    expect(requests.filter((r) => ARTIFACT.test(new URL(r.split(' ')[1]).pathname))).toEqual([]);
    const seen = requests.length;

    await page.getByRole('button', { name: 'Back to list' }).click();
    await expect(rows.nth(2)).toContainText('No longer available');
    await expect(rows.nth(2)).not.toContainText(target.controlNumber);
    await rows.nth(2).dblclick();
    await expect(viewer.getByRole('heading', { name: 'Document not available' })).toBeVisible();
    await page.waitForLoadState('networkidle');
    expect(requests).toHaveLength(seen);
  } finally {
    if (wallId) await admin.delete(`/security/walls/${wallId}`);
    await adminContext.close();
  }
});
