// Open-model search stream (E17-T04): SEARCH_RATE queries/s for DURATION, the §29 mix replayed from the query file.
// Example: k6 run -e QUERIES=/abs/queries.json -e BASE_URL=http://127.0.0.1:8080 -e SEARCH_RATE=50 -e DURATION=10m search-mix.js
import { config, summaryTrendStats, thresholds } from './lib/config.js';
import { searchScenario } from './lib/scenarios.js';

export { searchArrival } from './lib/scenarios.js';

export const options = {
  scenarios: { search: searchScenario(config.durations.main, '0s', config.phase) },
  thresholds: thresholds(),
  summaryTrendStats,
};

export default function () {}
