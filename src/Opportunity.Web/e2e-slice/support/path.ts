import { createHash } from 'node:crypto';
import type { Page } from '@playwright/test';
import { expect } from './fixtures';
import type { Api } from './slice';

// Steps of the §32 path shared by the mouse and the keyboard-only specs.

/** "Doc 2 of 23" in Review mode; "≈ 23" while the index is still catching up with a save (Q-10). */
export const docOf = (n: number, total: number) => new RegExp(`^Doc ${n} of (≈ )?${total}$`);

/** "Selected: all 23 results" (or "≈ 23", as above). */
export const allSelected = (total: number) => new RegExp(`^Selected: all (≈ )?${total} results$`);

/**
 * Waits for the frozen set Mass Edit creates (`POST …/snapshots`) and returns its id, from the Location header (the
 * browser may drop the body of a response the page already consumed).
 */
export function frozenSetCreated(page: Page): () => Promise<string> {
  const response = page.waitForResponse(
    (r) => r.request().method() === 'POST' && r.url().endsWith('/snapshots'),
  );
  return async () => {
    const created = await response;
    expect(created.status(), await created.text().catch(() => '')).toBeLessThan(300);
    const location = (await created.headerValue('location')) ?? '';
    const id = /\/snapshots\/([0-9a-f-]{36})$/.exec(location)?.[1];
    if (!id) throw new Error(`No frozen set in Location: '${location}'`);
    return id;
  };
}

/** Runs `query` from the keyword box with the Search button and waits for the list. */
export async function runSearch(page: Page, query: string): Promise<void> {
  const keyword = page.getByRole('textbox', { name: 'Keyword' });
  await keyword.fill(query);
  const searched = page.waitForResponse(
    (r) => r.request().method() === 'POST' && r.url().endsWith('/searches') && r.ok(),
  );
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await searched;
}

/**
 * Searches until the list shows `count` documents: coding is searchable within the freshness SLO (Q-10), not at once.
 * `submit` runs the search (the mouse and the keyboard paths differ).
 */
export async function searchUntil(
  page: Page,
  query: string,
  count: number,
  submit: (page: Page, query: string) => Promise<void> = runSearch,
): Promise<void> {
  const label = `${count} ${count === 1 ? 'document' : 'documents'}`;
  await expect(async () => {
    await submit(page, query);
    await expect(page.locator('.grid__count')).toContainText(label, { timeout: 2_000 });
  }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000] });
}

interface Snapshot {
  snapshotId: string;
  status: string;
  documentCount: number | null;
}

interface Export {
  exportId: string;
  status: string;
  statusReason: string | null;
  report: {
    documentsExported: number;
    documentsExcluded: number;
    texts: number;
    natives: number;
    files: number;
    totalBytes: number;
    manifestSha256: string;
  } | null;
}

interface ExportFile {
  fileId: string;
  path: string;
  kind: string;
  sizeBytes: number;
  sha256: string;
}

/**
 * Exports a frozen set (re-frozen for the Export purpose, Q-15), waits for the export job and downloads the package
 * and its DAT through the protected-content gateway. Checks the DAT lists exactly `controlNumbers` with the bulk
 * Issues value and the interactive coding, and the package carries the DAT and a manifest matching the report's SHA-256.
 */
export async function exportFrozenSet(
  api: Api,
  frozenSetId: string,
  controlNumbers: string[],
  codedControlNumber: string,
  values = { issue: 'Pricing', responsiveness: 'Responsive' },
): Promise<void> {
  let snapshot = await api.post<Snapshot>(
    '/snapshots',
    { purpose: 'export', snapshotId: frozenSetId },
    true,
  );
  if (snapshot.status !== 'ready') {
    snapshot = await api.waitFor<Snapshot>(
      `/snapshots/${snapshot.snapshotId}`,
      (s) => s.status === 'ready',
      { message: 'export snapshot ready' },
    );
  }
  expect(snapshot.documentCount).toBe(controlNumbers.length);

  const started = await api.post<Export>(
    '/exports',
    {
      snapshotId: snapshot.snapshotId,
      name: 'Vertical slice export',
      fields: [
        { fieldId: 1 },
        { fieldId: await fieldId(api, 'Responsiveness') },
        { fieldId: await fieldId(api, 'Issues') },
      ],
      includeImages: false,
    },
    true,
  );
  const done = await api.waitFor<Export>(
    `/exports/${started.exportId}`,
    (e) => e.status !== 'running',
    { message: 'export finished' },
  );
  expect(done.status, done.statusReason ?? '').toBe('completed');
  expect(done.report).toMatchObject({
    documentsExported: controlNumbers.length,
    documentsExcluded: 0,
    texts: controlNumbers.length,
  });

  const files = (
    await api.get<{ items: ExportFile[] }>(`/exports/${started.exportId}/files?limit=200`)
  ).items;
  const dat = files.find((f) => f.kind === 'dat');
  const manifest = files.find((f) => f.path === 'MANIFEST.json');
  expect(dat, 'the export has a DAT').toBeTruthy();
  expect(manifest, 'the export has a manifest').toBeTruthy();

  const datBytes = await (
    await api.getRaw(`/exports/${started.exportId}/files/${dat!.fileId}/content`)
  ).body();
  expect(sha256(datBytes)).toBe(dat!.sha256);
  const rows = datBytes.toString('utf8').replace(/^﻿/, '').split(/\r?\n/).filter(Boolean);
  const header = rows[0]!;
  expect(header).toContain('Issues');
  const exported = rows.slice(1).map((row) => controlNumbers.find((c) => row.includes(c)));
  expect(exported.filter(Boolean).sort()).toEqual([...controlNumbers].sort());
  expect(rows.slice(1).every((row) => row.includes(values.issue))).toBe(true);
  expect(rows.find((row) => row.includes(codedControlNumber))).toContain(values.responsiveness);

  const manifestBytes = await (
    await api.getRaw(`/exports/${started.exportId}/files/${manifest!.fileId}/content`)
  ).body();
  expect(sha256(manifestBytes)).toBe(done.report!.manifestSha256);

  // The whole package as one ZIP (entries stored uncompressed): it carries the DAT and the manifest byte for byte.
  const response = await api.getRaw(`/exports/${started.exportId}/package`);
  expect(response.headers()['content-type']).toBe('application/zip');
  const zip = await response.body();
  expect(zip.subarray(0, 4).toString('latin1')).toBe('PK\u0003\u0004');
  expect(zip.length).toBeGreaterThan(files.reduce((sum, f) => sum + f.sizeBytes, 0));
  expect(zip.indexOf(Buffer.from(dat!.path))).toBeGreaterThan(-1);
  expect(zip.indexOf(datBytes)).toBeGreaterThan(-1);
  expect(zip.indexOf(manifestBytes)).toBeGreaterThan(-1);
}

async function fieldId(api: Api, displayName: string): Promise<number> {
  const fields = await api.get<{ items: { fieldId: number; displayName: string }[] }>('/fields');
  const field = fields.items.find((f) => f.displayName === displayName);
  if (!field) throw new Error(`No field ${displayName}: run ./opportunity.sh seed`);
  return field.fieldId;
}

const sha256 = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
