import type { Route } from '@playwright/test';

/**
 * Export List (CSV) (#181) for the e2e mock API: `POST …/list-exports` (202 with the export, Idempotency-Key),
 * `GET …/list-exports/{id}` (running on the first read, completed with one document left out on the next) and the
 * CSV through the gateway (`…/content`, UTF-8 with BOM).
 */

export interface ListExportRequest {
  body: Record<string, unknown>;
  idempotencyKey: string | null;
}

const CSV = '\ufeffControl Number,Document Date,File Name\r\nACM0000001,2024-03-01,memo.msg\r\n';

export class ListExportsMock {
  readonly requests: ListExportRequest[] = [];
  private readonly reads = new Map<string, number>();
  private readonly counts = new Map<string, number>();

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const match = /^\/api\/v1\/workspaces\/[^/]+\/list-exports(?:\/([^/]+)(\/content)?)?$/.exec(
      path,
    );
    if (!match) return undefined;
    const [, id, content] = match;
    if (method === 'POST' && !id) {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      this.requests.push({
        body,
        idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
      });
      const exportId = `lx-${this.requests.length}`;
      const count = Array.isArray(body['documentIds']) ? body['documentIds'].length : 250;
      this.reads.set(exportId, 0);
      this.counts.set(exportId, count);
      return route.fulfill({ status: 202, json: resource(exportId, 'queued', count) });
    }
    if (method === 'GET' && id && this.reads.has(id)) {
      if (content) {
        return route.fulfill({
          status: 200,
          headers: {
            'Content-Type': 'text/csv; charset=utf-8',
            'Content-Disposition': `attachment; filename="export-list-${id}.csv"`,
            'Cache-Control': 'no-store',
          },
          body: CSV,
        });
      }
      const reads = this.reads.get(id)!;
      this.reads.set(id, reads + 1);
      const count = this.counts.get(id)!;
      return route.fulfill({
        json: reads === 0 ? resource(id, 'running', count) : resource(id, 'completed', count),
      });
    }
    return undefined;
  }
}

function resource(exportId: string, status: string, count: number) {
  const done = status === 'completed';
  return {
    exportId,
    jobId: `job-${exportId}`,
    name: 'Export List 2026-10-11 10:42 UTC',
    status,
    statusReason: null,
    snapshotId: `snapshot-${exportId}`,
    documentCount: count,
    columns: [],
    createdAt: '2026-10-11T10:42:00Z',
    completedAt: done ? '2026-10-11T10:42:05Z' : null,
    rowCount: done ? Math.max(0, count - 1) : null,
    documentsExcluded: done ? 1 : null,
    byteCount: done ? 12_700 : null,
    contentSha256: done ? 'a'.repeat(64) : null,
  };
}
