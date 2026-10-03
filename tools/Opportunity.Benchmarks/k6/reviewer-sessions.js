// Closed-model reviewer sessions (E17-T04): REVIEWERS concurrent reviewers for DURATION, each repeating
// search -> open 5..25 documents -> read (log-normal think time, median 20 s, p90 60 s) -> code -> 5 s pause.
// Example: k6 run -e QUERIES=/abs/queries.json -e REVIEWERS=100 -e DURATION=30m reviewer-sessions.js
import { config, summaryTrendStats, thresholds } from './lib/config.js';
import { reviewerScenario } from './lib/scenarios.js';

export { reviewerSession } from './lib/scenarios.js';

export const options = {
  scenarios: { reviewers: reviewerScenario(config.durations.main, '0s', { PHASE: config.phase }) },
  thresholds: thresholds(),
  summaryTrendStats,
};

export default function () {}
