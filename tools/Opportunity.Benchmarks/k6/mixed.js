// Mixed scenario for the §26 degradation gates (E17-T04, baseline §29):
//   phase "idle" (IDLE_DURATION): open-model search stream + REVIEWERS reviewer sessions, no bulk load
//   phase "bulk" (BULK_DURATION): the same search stream (same query sequence, from schedule position 0)
//                                 + reviewers + bulk coding at BULK_DOCS_PER_SEC
// ingest-k6 maps phase idle -> scenario role idle-baseline and bulk -> bulk-load.
// Example: k6 run -e QUERIES=/abs/queries.json -e SEARCH_RATE=50 -e REVIEWERS=100 -e BULK_DOCS_PER_SEC=2000 \
//                 -e IDLE_DURATION=15m -e BULK_DURATION=15m mixed.js
import { config, summaryTrendStats, thresholds } from './lib/config.js';
import { bulkScenario, reviewerScenario, runSetup, searchScenario } from './lib/scenarios.js';

export { bulkArrival, reviewerSession, searchArrival } from './lib/scenarios.js';

function milliseconds(duration) {
  const match = /^(\d+(?:\.\d+)?)(ms|s|m|h)$/.exec(duration);
  if (!match) {
    throw new Error(`unsupported duration '${duration}' (use e.g. 900s, 15m, 1h)`);
  }
  return Number(match[1]) * { ms: 1, s: 1000, m: 60000, h: 3600000 }[match[2]];
}

const idleMs = milliseconds(config.durations.idle);
const bulkMs = milliseconds(config.durations.bulk);

const scenarios = {
  search_idle: searchScenario(config.durations.idle, '0s', 'idle'),
  search_bulk: searchScenario(config.durations.bulk, `${idleMs}ms`, 'bulk'),
  bulk: bulkScenario(config.durations.bulk, `${idleMs}ms`, 'bulk'),
};
if (config.reviewers.count > 0) {
  scenarios.reviewers = reviewerScenario(`${idleMs + bulkMs}ms`, '0s', {
    PHASE_SPLIT_MS: String(idleMs),
    PHASE_BEFORE: 'idle',
    PHASE_AFTER: 'bulk',
  });
}

export const options = { scenarios, thresholds: thresholds(), summaryTrendStats };

export function setup() {
  return runSetup();
}

export default function () {}
