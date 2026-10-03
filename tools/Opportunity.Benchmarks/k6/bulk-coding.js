// Background bulk-coding driver (E17-T04): BULK_DOCS_PER_SEC documents/s offered as bulk-coding jobs of BULK_BATCH
// documents (control-number ranges), for DURATION. Each job carries a unique Idempotency-Key.
// Example: k6 run -e QUERIES=/abs/queries.json -e BULK_DOCS_PER_SEC=2000 -e BULK_BATCH=500 -e DURATION=10m bulk-coding.js
import { config, summaryTrendStats, thresholds } from './lib/config.js';
import { bulkScenario, runSetup } from './lib/scenarios.js';

export { bulkArrival } from './lib/scenarios.js';

export const options = {
  scenarios: { bulk: bulkScenario(config.durations.main, '0s', config.phase) },
  thresholds: thresholds(),
  summaryTrendStats,
};

export function setup() {
  return runSetup();
}

export default function () {}
