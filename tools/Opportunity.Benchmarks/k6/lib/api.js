// API calls of the benchmark workloads, written against the ADR-019 conventions. Request/response shapes that the M1
// API has not fixed yet (search body, coding body, bulk job body) live only here, so adapting them is a one-file change.
//
// Every call records one sample of the `opp_request` trend (milliseconds) tagged with:
//   op      search | document-view | coding-write | bulk-submit
//   qclass  taxonomy class (search only), gate simple|complex, bucket (§29 mix bucket)
//   phase   idle | bulk | main ...  (see workload.js)
//   source  open (arrival-rate stream) | session (reviewer) | bulk
//   outcome ok | error | conflict (412 on coding: a concurrent change, reported but not an error)
// `opportunity-bench ingest-k6` turns these samples into HDR histograms per class and phase.

import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

import { config, url } from './config.js';
import { currentPhase, querySet } from './workload.js';

export const requestTrend = new Trend('opp_request', true);

function headers(extra) {
  const h = Object.assign({ Accept: 'application/json' }, extra || {});
  if (config.tokens.length > 0) {
    h.Authorization = `Bearer ${config.tokens[(exec.vu.idInTest - 1) % config.tokens.length]}`;
  }
  return h;
}

function record(res, tags, okStatuses, conflictStatus) {
  const outcome = okStatuses.includes(res.status) ? 'ok' : res.status === conflictStatus ? 'conflict' : 'error';
  requestTrend.add(res.timings.duration, Object.assign({ outcome, phase: currentPhase() }, tags));
  check(res, { [`${tags.op} status ${okStatuses.join('/')}`]: () => outcome !== 'error' });
  return outcome;
}

// POST .../searches?limit=50 — returns the parsed body when `parse` is set (reviewers need the document ids).
export function search(entry, source, parse) {
  const q = entry.query;
  const body = {
    query: q.oql,
    highlight: querySet.highlight,
    zone: querySet.zone,
    sort: q.sort,
    facets: q.facets,
    expand: q.expand === undefined ? null : q.expand,
  };
  const res = http.post(`${url(config.paths.search)}?limit=${querySet.pageSize}`, JSON.stringify(body), {
    headers: headers({ 'Content-Type': 'application/json', 'X-Bench-Query': q.id, 'X-Bench-Seq': String(entry.position) }),
    tags: { name: 'search' },
    responseType: parse ? 'text' : 'none',
  });
  const outcome = record(res, { op: 'search', qclass: q.class, gate: q.gate, bucket: q.bucket, source }, [200], 0);
  if (!parse || outcome !== 'ok') {
    return { outcome, items: [] };
  }
  const json = res.json();
  return { outcome, items: (json && json.items) || [], total: json && json.total };
}

export function documentIdOf(item) {
  return item.documentId || item.id;
}

// GET .../documents/{documentId} — the ETag (DocumentVersion) is sent back as If-Match when coding (ADR-019 §2.7).
export function viewDocument(documentId) {
  const res = http.get(url(config.paths.document, { documentId }), {
    headers: headers(),
    tags: { name: 'document-view' },
    responseType: 'none',
  });
  const outcome = record(res, { op: 'document-view', source: 'session' }, [200], 0);
  check(res, { 'document-view returns an ETag': (r) => outcome !== 'ok' || !!r.headers.Etag });
  return { outcome, etag: res.headers.Etag };
}

// PUT .../documents/{documentId}/coding with If-Match (E10: optimistic concurrency on DocumentVersion).
export function codeDocument(documentId, etag, changes) {
  const res = http.put(url(config.paths.coding, { documentId }), JSON.stringify({ changes }), {
    headers: headers({ 'Content-Type': 'application/json', 'If-Match': etag || '*' }),
    tags: { name: 'coding-write' },
    responseType: 'none',
  });
  return record(res, { op: 'coding-write', source: 'session' }, [200, 204], 412);
}

// POST .../bulk-coding-jobs with Idempotency-Key (ADR-019 §2.5-2.6): 202 Accepted + Location of the job.
export function submitBulkJob(targetQuery, changes, idempotencyKey) {
  const res = http.post(url(config.paths.bulkJobs), JSON.stringify({ target: { query: targetQuery }, changes }), {
    headers: headers({ 'Content-Type': 'application/json', 'Idempotency-Key': idempotencyKey }),
    tags: { name: 'bulk-submit' },
    responseType: 'none',
  });
  const outcome = record(res, { op: 'bulk-submit', source: 'bulk' }, [202], 0);
  check(res, { 'bulk job has a Location': (r) => outcome !== 'ok' || !!r.headers.Location });
  return outcome;
}
