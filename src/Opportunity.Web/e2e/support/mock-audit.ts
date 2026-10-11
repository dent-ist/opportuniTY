import type { Route } from '@playwright/test';

/**
 * The audit log (E14-T04) for the e2e mock API, in the shapes of `GET …/audit-events` (filters, newest first, cursor
 * pages of 2), `GET …/audit-actions`, `GET/POST …/audit-exports` (202 with the export, Idempotency-Key),
 * `GET …/audit-exports/{id}` (a requested export completes on its next read) and the CSV through the gateway; plus
 * `GET …/documents/{id}/coding-history` for the viewer's History tab.
 */

export interface AuditLogRequest {
  method: string;
  path: string;
  query: string;
  body: Record<string, unknown> | null;
  idempotencyKey: string | null;
}

const ALEX = { type: 'user', id: 'user-1', displayName: 'Alex Reviewer' };
const JAMIE = { type: 'user', id: 'user-2', displayName: 'Jamie Lee' };

function event(n: number, extra: Record<string, unknown>) {
  return {
    eventId: `audit-${n}`,
    occurredAt: `2026-10-10T09:${String(n).padStart(2, '0')}:00Z`,
    recordedAt: `2026-10-10T09:${String(n).padStart(2, '0')}:00Z`,
    category: 'Document',
    action: 'Viewed',
    outcome: 'success',
    reasonCode: null,
    actor: ALEX,
    onBehalfOf: null,
    accessPath: 'normal',
    resource: { type: 'Document', id: `doc-${n}`, controlNumber: `ACM000000${n}` },
    jobId: null,
    chunkSequence: null,
    snapshotId: null,
    searchGeneration: null,
    correlationId: `corr-${n}`,
    clientIp: '203.0.113.7',
    details: { Purpose: 'Display' },
    searchText: null,
    sequence: n,
    ...extra,
  };
}

const EVENTS = [
  event(6, {
    category: 'Search',
    action: 'Executed',
    resource: { type: 'Search', id: 'search-1', controlNumber: null },
    searchGeneration: 42,
    details: { Hits: '12' },
    searchText: { query: 'pricing AND agreement' },
  }),
  event(5, {
    category: 'Coding',
    action: 'BulkChunkApplied',
    actor: JAMIE,
    resource: { type: 'Job', id: 'job-bulk-7', controlNumber: null },
    jobId: 'job-bulk-7',
    chunkSequence: 1,
    details: { Documents: '250' },
  }),
  event(4, { actor: JAMIE }),
  event(3, {
    category: 'AuthZ',
    action: 'Denied',
    outcome: 'denied',
    reasonCode: 'PermissionNotGranted',
    actor: { type: 'user', id: 'user-9', displayName: 'Sam Outsider' },
    resource: { type: 'Workspace', id: 'ws-1', controlNumber: null },
    details: { Permission: 'Audit.Read' },
  }),
  event(2, { category: 'Coding', action: 'Changed' }),
  event(1, { category: 'Document', action: 'NativeDownloaded' }),
];

const ACTIONS = [
  ['Document', 'Viewed'],
  ['Document', 'NativeDownloaded'],
  ['Coding', 'Changed'],
  ['Coding', 'BulkChunkApplied'],
  ['Search', 'Executed'],
  ['AuthZ', 'Denied'],
  ['Audit', 'Queried'],
  ['Audit', 'Exported'],
].map(([category, action]) => ({ category, action }));

const HISTORY = [
  {
    eventId: 'ce-3',
    occurredAt: '2026-10-09T15:00:00Z',
    fieldId: 1000,
    fieldName: 'Responsiveness',
    kind: 'changed',
    oldValue: 1,
    newValue: 2,
    documentVersion: 3,
    actor: { userId: 'user-2', displayName: 'Jamie Lee' },
    actorType: 'bulkHuman',
    source: 'job',
    jobId: 'job-bulk-7',
    originEventId: null,
  },
  {
    eventId: 'ce-2',
    occurredAt: '2026-10-08T11:30:00Z',
    fieldId: 1000,
    fieldName: 'Responsiveness',
    kind: 'changed',
    oldValue: null,
    newValue: 1,
    documentVersion: 2,
    actor: { userId: 'user-1', displayName: 'Alex Reviewer' },
    actorType: 'human',
    source: 'interactive',
    jobId: null,
    originEventId: null,
  },
];

export class AuditLogMock {
  readonly requests: AuditLogRequest[] = [];
  private readonly exports: Record<string, unknown>[] = [];
  private next = 1;

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const history = /^\/api\/v1\/workspaces\/[^/]+\/documents\/([^/]+)\/coding-history$/.exec(path);
    if (history && method === 'GET') {
      const items = url.searchParams.get('cursor') ? [] : HISTORY;
      return route.fulfill({
        json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
      });
    }
    const match =
      /^\/api\/v1\/workspaces\/[^/]+\/(audit-events|audit-actions|audit-exports)(?:\/([^/]+)(\/content)?)?$/.exec(
        path,
      );
    if (!match) return undefined;
    const [, resource, id, content] = match;
    const body =
      method === 'POST' ? (route.request().postDataJSON() as Record<string, unknown>) : null;
    this.requests.push({
      method,
      path,
      query: url.search,
      body,
      idempotencyKey: route.request().headers()['idempotency-key'] ?? null,
    });
    if (resource === 'audit-actions') return route.fulfill({ json: ACTIONS });
    if (resource === 'audit-events') {
      const q = url.searchParams;
      const items = EVENTS.filter(
        (e) =>
          (!q.get('category') || e.category === q.get('category')) &&
          (!q.get('action') || e.action === q.get('action')) &&
          (!q.get('actorId') || e.actor.id === q.get('actorId')) &&
          (!q.get('outcome') || e.outcome === q.get('outcome')) &&
          (!q.get('jobId') || e.jobId === q.get('jobId')) &&
          (!q.get('controlNumber') || e.resource.controlNumber === q.get('controlNumber')),
      ).map((e) => (q.get('includeSearchText') === 'true' ? e : { ...e, searchText: null }));
      const start = Number(q.get('cursor') ?? 0);
      const page = items.slice(start, start + 2);
      const next = start + 2 < items.length ? String(start + 2) : null;
      return route.fulfill({
        json: { items: page, nextCursor: next, total: { value: page.length, relation: 'gte' } },
      });
    }
    if (method === 'POST' && !id) {
      const created = {
        exportId: `audit-export-${this.next}`,
        jobId: `job-audit-${this.next++}`,
        status: 'queued',
        statusReason: null,
        filters: {
          from: body?.['from'] ?? null,
          to: body?.['to'] ?? null,
          category: body?.['category'] ?? null,
          action: body?.['action'] ?? null,
          actorId: body?.['actorId'] ?? null,
          documentId: null,
          jobId: body?.['jobId'] ?? null,
          outcome: body?.['outcome'] ?? null,
        },
        includeSearchText: body?.['includeSearchText'] === true,
        occurredBefore: '2026-10-10T10:00:00Z',
        createdAt: '2026-10-10T10:00:00Z',
        completedAt: null,
        rowCount: null,
        byteCount: null,
        contentSha256: null,
      };
      this.exports.unshift(created);
      return route.fulfill({
        status: 202,
        headers: { location: `/api/v1/workspaces/ws-1/jobs/${created.jobId}` },
        json: created,
      });
    }
    if (method !== 'GET') return undefined;
    if (!id) {
      return route.fulfill({
        json: {
          items: this.exports,
          nextCursor: null,
          total: { value: this.exports.length, relation: 'eq' },
        },
      });
    }
    const index = this.exports.findIndex((e) => e['exportId'] === id);
    if (index < 0)
      return route.fulfill({
        status: 404,
        contentType: 'application/problem+json',
        body: JSON.stringify({ status: 404, code: 'not-found', title: 'Not found' }),
      });
    if (content) {
      return route.fulfill({
        contentType: 'text/csv; charset=utf-8',
        headers: { 'content-disposition': 'attachment; filename="audit-log.csv"' },
        body: '﻿Occurred At (UTC),Event ID,Category,Action\r\n',
      });
    }
    // A requested export completes when it is read again (the job ran meanwhile).
    const completed = {
      ...this.exports[index],
      status: 'completed',
      completedAt: '2026-10-10T10:00:05Z',
      rowCount: 2,
      byteCount: 640,
      contentSha256: 'ab'.repeat(32),
    };
    this.exports[index] = completed;
    return route.fulfill({ json: completed });
  }
}
