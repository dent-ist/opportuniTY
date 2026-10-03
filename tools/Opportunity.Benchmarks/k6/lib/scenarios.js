// Scenario bodies shared by the standalone scripts and mixed.js (E17-T04).

import exec from 'k6/execution';
import { sleep } from 'k6';

import { codeDocument, documentIdOf, search, submitBulkJob, viewDocument } from './api.js';
import { config } from './config.js';
import { codingField, controlNumber, pickWeighted, queryAt, querySet, reviewerQuery, rng, session, thinkTimeSeconds } from './workload.js';

// Open model: one search per arrival. iterationInTest is unique per scenario, so the query sequence is fixed by the seed.
export function searchArrival() {
  search(queryAt(exec.scenario.iterationInTest), 'open', false);
}

// Closed model: one reviewer session = search, open 5..25 of the hits (one page at most), read, code each, pause.
export function reviewerSession() {
  const vu = exec.vu.idInTest;
  const k = exec.vu.iterationInScenario;
  const random = rng(vu, k);
  const result = search(reviewerQuery(vu, k), 'session', true);
  const open = Math.min(result.items.length, random.int(session.minDocuments, session.maxDocuments));
  const responsiveness = codingField('responsiveness');
  const issues = codingField('issues');
  for (let i = 0; i < open; i++) {
    const documentId = documentIdOf(result.items[i]);
    const view = viewDocument(documentId);
    sleep(thinkTimeSeconds(random) * config.thinkScale);
    if (view.outcome !== 'ok') {
      continue;
    }
    const changes = [{ field: 'responsiveness', op: 'set', value: pickWeighted(random, responsiveness.choices) }];
    const issue = issues.choices[random.int(0, issues.choices.length - 1)];
    if (random.next() < 0.3) {
      changes.push({ field: 'issues', op: 'add', value: issue.name });
    }
    codeDocument(documentId, view.etag, changes);
  }
  sleep(session.pauseBetweenSearchesSeconds * config.thinkScale);
}

// Bulk-coding driver at a fixed offered rate: job i tags the control-number range of batch i (cycling the corpus) with
// one bench_bulk_tag choice. Idempotency keys are unique per run (setup data) and iteration.
export function bulkArrival(data) {
  const i = exec.scenario.iterationInTest;
  const batch = config.bulk.batchDocs;
  const total = querySet.documentCount;
  const batches = Math.max(1, Math.floor(total / batch));
  const first = (i % batches) * batch + 1;
  const last = Math.min(total, first + batch - 1);
  const tag = codingField('bench_bulk_tag').choices[i % 8].name;
  submitBulkJob(
    `controlnumber:[${controlNumber(first)} TO ${controlNumber(last)}]`,
    [{ field: 'bench_bulk_tag', op: 'add', value: tag }],
    `${data.runId}-bulk-${i}`,
  );
}

export function runSetup() {
  return { runId: __ENV.RUN_ID || `bench-${Date.now().toString(36)}` };
}

// Open-model search stream. Offered rate = SEARCH_RATE queries/s.
export function searchScenario(duration, startTime, phase) {
  return {
    executor: 'constant-arrival-rate',
    exec: 'searchArrival',
    rate: config.search.rate,
    timeUnit: '1s',
    duration,
    startTime,
    preAllocatedVUs: config.search.preAllocatedVUs,
    maxVUs: config.search.maxVUs,
    env: { PHASE: phase },
    tags: { phase },
  };
}

// Offered bulk rate: BULK_DOCS_PER_SEC documents/s as jobs of BULK_BATCH documents (rate per BULK_BATCH seconds).
export function bulkScenario(duration, startTime, phase) {
  return {
    executor: 'constant-arrival-rate',
    exec: 'bulkArrival',
    rate: config.bulk.docsPerSecond,
    timeUnit: `${config.bulk.batchDocs}s`,
    duration,
    startTime,
    preAllocatedVUs: config.bulk.preAllocatedVUs,
    maxVUs: config.bulk.maxVUs,
    env: { PHASE: phase },
    tags: { phase },
  };
}

export function reviewerScenario(duration, startTime, env) {
  return {
    executor: 'constant-vus',
    exec: 'reviewerSession',
    vus: config.reviewers.count,
    duration,
    startTime,
    gracefulStop: '30s',
    env,
  };
}
